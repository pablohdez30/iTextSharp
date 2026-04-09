// ============================================================================
//  XfaPdfFiller - Biblioteca para rellenar formularios XFA en documentos PDF
//  Reemplazo de iTextSharp sin dependencias externas
// ----------------------------------------------------------------------------
//  09/04/2026 PHP:
//  - Permite inyectar datos XML en formularios PDF con estructura XFA,
//    parseando y reescribiendo el binario PDF mediante actualizacion incremental.
//  - Este fichero implementa el lector de documentos PDF. Parsea las tablas de
//    referencias cruzadas (xref tradicional y xref streams), resuelve objetos
//    indirectos y comprimidos, descomprime streams FlateDecode con soporte de
//    predictores PNG/TIFF, y localiza la estructura XFA dentro del catalogo
//    del PDF (Catalog -> AcroForm -> XFA).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XfaPdfFiller
{
    internal class PdfDocumentReader
    {
        private readonly byte[] _data;
        private readonly PdfTokenizer _tokenizer;
        private readonly Dictionary<int, XrefEntry> _xref = new Dictionary<int, XrefEntry>();
        private PdfDictionary _trailer;
        private readonly Dictionary<int, PdfObject> _objectCache = new Dictionary<int, PdfObject>();

        public PdfDocumentReader(byte[] data)
        {
            _data = data;
            _tokenizer = new PdfTokenizer(data);
            ReadXrefAndTrailer();
        }

        public PdfDictionary Trailer => _trailer ?? throw new InvalidOperationException("No trailer found");
        public Dictionary<int, XrefEntry> Xref => _xref;

        private void ReadXrefAndTrailer()
        {
            // Find startxref
            var pattern = Encoding.ASCII.GetBytes("startxref");
            int pos = _tokenizer.FindBackward(pattern, -1);
            if (pos < 0)
                throw new InvalidOperationException("Cannot find startxref in PDF");

            _tokenizer.Position = pos + pattern.Length;
            _tokenizer.SkipWhitespaceAndComments();

            var token = _tokenizer.NextToken();
            long xrefOffset = long.Parse(token.Value);

            ReadXrefAt(xrefOffset);
        }

        private void ReadXrefAt(long offset)
        {
            _tokenizer.Position = (int)offset;
            _tokenizer.SkipWhitespaceAndComments();

            // Check if this is a traditional xref table or a cross-reference stream
            var token = _tokenizer.NextToken();

            if (token.Type == PdfTokenType.Keyword && token.Value == "xref")
            {
                ReadTraditionalXref();
            }
            else if (token.Type == PdfTokenType.Number)
            {
                // Cross-reference stream (PDF 1.5+)
                // token is the object number
                ReadXrefStream(offset);
            }
            else
            {
                throw new InvalidOperationException($"Unexpected token at xref offset: {token}");
            }
        }

        private void ReadTraditionalXref()
        {
            // Read xref subsections
            while (true)
            {
                _tokenizer.SkipWhitespaceAndComments();
                var token = _tokenizer.NextToken();

                if (token.Type == PdfTokenType.Keyword && token.Value == "trailer")
                    break;

                if (token.Type != PdfTokenType.Number)
                    throw new InvalidOperationException($"Expected subsection start number, got: {token}");

                int startObj = int.Parse(token.Value);
                token = _tokenizer.NextToken();
                int count = int.Parse(token.Value);

                for (int i = 0; i < count; i++)
                {
                    token = _tokenizer.NextToken();
                    long entryOffset = long.Parse(token.Value);
                    token = _tokenizer.NextToken();
                    int gen = int.Parse(token.Value);
                    token = _tokenizer.NextToken();
                    bool inUse = token.Value == "n";

                    int objNum = startObj + i;
                    if (!_xref.ContainsKey(objNum))
                    {
                        _xref[objNum] = new XrefEntry
                        {
                            Offset = entryOffset,
                            Generation = gen,
                            InUse = inUse,
                            ObjectNumber = objNum
                        };
                    }
                }
            }

            // Read trailer dictionary
            _tokenizer.SkipWhitespaceAndComments();
            var trailerDict = ParseObject() as PdfDictionary;
            if (_trailer == null)
                _trailer = trailerDict;

            // Follow Prev pointer for incremental updates
            if (trailerDict != null)
            {
                var prev = trailerDict.Get("Prev");
                if (prev is PdfNumber prevNum)
                {
                    ReadXrefAt((long)prevNum.Value);
                }
            }
        }

        private void ReadXrefStream(long offset)
        {
            _tokenizer.Position = (int)offset;

            // Parse: objNum gen obj << ... >> stream ... endstream endobj
            var token = _tokenizer.NextToken(); // obj number
            _tokenizer.NextToken(); // generation
            _tokenizer.NextToken(); // "obj"

            var dictObj = ParseObject();
            var dict = dictObj as PdfDictionary;
            if (dict == null)
                throw new InvalidOperationException("Expected dictionary for xref stream");

            // Find stream data using robust byte scanning
            int streamStart = FindStreamKeyword(_tokenizer.Position);
            if (streamStart < 0)
                throw new InvalidOperationException("Cannot find stream keyword in xref stream object");
            _tokenizer.Position = streamStart;
            byte[] streamData = ReadStreamDataDirect(dict);
            byte[] decompressed = DecompressStream(dict, streamData);

            // The xref stream dictionary IS the trailer
            if (_trailer == null)
                _trailer = dict;

            // Parse W array (field widths)
            var wArray = dict.Get("W") as PdfArray;
            if (wArray == null || wArray.Items.Count < 3)
                throw new InvalidOperationException("Invalid xref stream: missing W array");

            int w0 = ((PdfNumber)wArray.Items[0]).IntValue;
            int w1 = ((PdfNumber)wArray.Items[1]).IntValue;
            int w2 = ((PdfNumber)wArray.Items[2]).IntValue;
            int entrySize = w0 + w1 + w2;

            // Parse Index array (default: [0 Size])
            List<int> indexEntries = new List<int>();
            var indexArray = dict.Get("Index") as PdfArray;
            if (indexArray != null)
            {
                foreach (var item in indexArray.Items)
                    indexEntries.Add(((PdfNumber)item).IntValue);
            }
            else
            {
                var size = dict.Get("Size") as PdfNumber;
                indexEntries.Add(0);
                indexEntries.Add(size?.IntValue ?? 0);
            }

            // Parse entries
            int dataPos = 0;
            for (int s = 0; s < indexEntries.Count; s += 2)
            {
                int startObj = indexEntries[s];
                int count = indexEntries[s + 1];

                for (int i = 0; i < count; i++)
                {
                    if (dataPos + entrySize > decompressed.Length) break;

                    int type = w0 > 0 ? ReadIntFromBytes(decompressed, dataPos, w0) : 1;
                    int field1 = ReadIntFromBytes(decompressed, dataPos + w0, w1);
                    int field2 = w2 > 0 ? ReadIntFromBytes(decompressed, dataPos + w0 + w1, w2) : 0;

                    int objNum = startObj + i;
                    if (!_xref.ContainsKey(objNum))
                    {
                        switch (type)
                        {
                            case 0: // free object
                                _xref[objNum] = new XrefEntry { ObjectNumber = objNum, InUse = false, Generation = field2 };
                                break;
                            case 1: // regular object
                                _xref[objNum] = new XrefEntry { ObjectNumber = objNum, Offset = field1, Generation = field2, InUse = true };
                                break;
                            case 2: // compressed object in object stream
                                _xref[objNum] = new XrefEntry
                                {
                                    ObjectNumber = objNum,
                                    InUse = true,
                                    IsCompressed = true,
                                    StreamObjectNumber = field1,
                                    IndexInStream = field2
                                };
                                break;
                        }
                    }

                    dataPos += entrySize;
                }
            }

            // Follow Prev pointer
            var prev = dict.Get("Prev");
            if (prev is PdfNumber prevNum)
            {
                ReadXrefAt((long)prevNum.Value);
            }
        }

        private static int ReadIntFromBytes(byte[] data, int offset, int width)
        {
            int result = 0;
            for (int i = 0; i < width; i++)
            {
                result = (result << 8) | data[offset + i];
            }
            return result;
        }

        public PdfObject ReadObject(int objNum)
        {
            if (_objectCache.TryGetValue(objNum, out var cached))
                return cached;

            if (!_xref.TryGetValue(objNum, out var entry) || !entry.InUse)
            {
                // Fallback: scan the PDF for "objNum 0 obj" if not found in xref.
                // This handles PDFs with incomplete/corrupt xref tables.
                int foundOffset = ScanForObject(objNum);
                if (foundOffset >= 0)
                {
                    entry = new XrefEntry { ObjectNumber = objNum, Offset = foundOffset, Generation = 0, InUse = true };
                    _xref[objNum] = entry;
                }
                else
                {
                    return PdfNull.Instance;
                }
            }

            if (entry.IsCompressed)
            {
                var obj = ReadCompressedObject(entry);
                _objectCache[objNum] = obj;
                return obj;
            }

            int objOffset = (int)entry.Offset;
            _tokenizer.Position = objOffset;

            // Parse: objNum gen obj <value> endobj
            var token = _tokenizer.NextToken(); // obj number
            _tokenizer.NextToken(); // generation
            _tokenizer.NextToken(); // "obj"

            var value = ParseObject();

            // Check if it's followed by a stream
            if (value is PdfDictionary dict)
            {
                // Search for "stream" keyword from the xref offset, scanning the full object.
                // We use the xref offset as base because the tokenizer position after
                // ParseObject can be unreliable for complex dictionaries.
                int streamStart = FindStreamKeyword(objOffset);
                if (streamStart >= 0)
                {
                    _tokenizer.Position = streamStart;
                    byte[] streamData = ReadStreamDataDirect(dict);
                    var stream = new PdfStream(dict, streamData);
                    _objectCache[objNum] = stream;
                    return stream;
                }
            }

            _objectCache[objNum] = value;
            return value;
        }

        // Scan forward from a position to find "stream" keyword (not "endstream") followed by EOL.
        // Returns the position right after the EOL (start of stream data), or -1 if not found.
        private int FindStreamKeyword(int fromPos)
        {
            // Scan up to 8KB - enough for even very large dictionaries
            int maxScan = Math.Min(fromPos + 8192, _data.Length - 6);
            for (int i = fromPos; i < maxScan; i++)
            {
                // Stop early if we hit "endobj" before finding "stream"
                if (_data[i] == 'e' && i + 6 <= _data.Length &&
                    _data[i + 1] == 'n' && _data[i + 2] == 'd' &&
                    _data[i + 3] == 'o' && _data[i + 4] == 'b' && _data[i + 5] == 'j')
                {
                    return -1;
                }

                if (_data[i] == 's' && i + 6 <= _data.Length &&
                    _data[i + 1] == 't' && _data[i + 2] == 'r' &&
                    _data[i + 3] == 'e' && _data[i + 4] == 'a' && _data[i + 5] == 'm')
                {
                    // Make sure it's not "endstream"
                    if (i > 0 && _data[i - 1] == 'd')
                        continue;

                    // Verify next char is whitespace (CR, LF, or space) - per PDF spec
                    int afterKeyword = i + 6;
                    if (afterKeyword < _data.Length &&
                        _data[afterKeyword] != '\r' && _data[afterKeyword] != '\n' &&
                        _data[afterKeyword] != ' ')
                        continue;

                    int pos = afterKeyword;
                    // Skip EOL: CR, LF, or CRLF
                    if (pos < _data.Length && _data[pos] == '\r') pos++;
                    if (pos < _data.Length && _data[pos] == '\n') pos++;
                    return pos;
                }
            }
            return -1;
        }

        // Fallback: scan the entire PDF for "objNum 0 obj" pattern.
        // Used when an object is referenced but not found in any xref table/stream.
        private int ScanForObject(int objNum)
        {
            byte[] pattern = Encoding.ASCII.GetBytes($"{objNum} 0 obj");
            for (int i = 0; i < _data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (_data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    // Verify it's at a line boundary (preceded by whitespace or start of file)
                    if (i == 0 || PdfTokenizer.IsWhitespace(_data[i - 1]))
                        return i;
                }
            }
            return -1;
        }

        private PdfObject ReadCompressedObject(XrefEntry entry)
        {
            // Guard: the object stream container must not itself be compressed
            if (_xref.TryGetValue(entry.StreamObjectNumber, out var containerEntry) && containerEntry.IsCompressed)
                throw new InvalidOperationException(
                    $"Object stream container {entry.StreamObjectNumber} is itself compressed (circular reference)");

            // Read the object stream container
            var streamObj = ReadObject(entry.StreamObjectNumber);
            var objStream = streamObj as PdfStream;
            if (objStream == null)
            {
                // Provide diagnostic info
                string actualType = streamObj != null ? streamObj.GetType().Name : "null";
                string hasXref = _xref.ContainsKey(entry.StreamObjectNumber) ? "yes" : "no";
                long offset = _xref.ContainsKey(entry.StreamObjectNumber) ? _xref[entry.StreamObjectNumber].Offset : -1;
                throw new InvalidOperationException(
                    $"Object stream {entry.StreamObjectNumber} is {actualType} (not a stream). " +
                    $"In xref: {hasXref}, offset: {offset}. " +
                    $"Trying to read compressed object {entry.ObjectNumber}.");
            }

            byte[] decompressed = DecompressStream(objStream.Dictionary, objStream.RawData);

            // Parse the object stream header
            var first = objStream.Dictionary.Get("First") as PdfNumber;
            int firstOffset = first?.IntValue ?? 0;

            var n = objStream.Dictionary.Get("N") as PdfNumber;
            int count = n?.IntValue ?? 0;

            // Parse object number/offset pairs from the header
            var headerTokenizer = new PdfTokenizer(decompressed);
            int[] objNumbers = new int[count];
            int[] offsets = new int[count];
            for (int i = 0; i < count; i++)
            {
                var tok = headerTokenizer.NextToken();
                objNumbers[i] = int.Parse(tok.Value);
                tok = headerTokenizer.NextToken();
                offsets[i] = int.Parse(tok.Value);
            }

            // Find the object at the given index
            int targetIndex = entry.IndexInStream;
            if (targetIndex >= count)
                return PdfNull.Instance;

            int objOffset = firstOffset + offsets[targetIndex];
            var objTokenizer = new PdfTokenizer(decompressed);
            objTokenizer.Position = objOffset;
            return ParseObjectWith(objTokenizer);
        }

        public PdfObject ResolveReference(PdfObject obj)
        {
            if (obj is PdfReference r)
                return ReadObject(r.ObjectNumber);
            return obj;
        }

        public PdfObject ParseObject() => ParseObjectWith(_tokenizer);

        private PdfObject ParseObjectWith(PdfTokenizer tokenizer)
        {
            tokenizer.SkipWhitespaceAndComments();
            var token = tokenizer.NextToken();

            switch (token.Type)
            {
                case PdfTokenType.Number:
                {
                    // Could be a number or start of a reference (N G R)
                    int savedPos = tokenizer.Position;
                    var next = tokenizer.NextToken();
                    if (next.Type == PdfTokenType.Number)
                    {
                        var third = tokenizer.NextToken();
                        if (third.Type == PdfTokenType.Keyword && third.Value == "R")
                        {
                            return new PdfReference(int.Parse(token.Value), int.Parse(next.Value));
                        }
                    }
                    tokenizer.Position = savedPos;
                    return new PdfNumber(double.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture));
                }

                case PdfTokenType.Name:
                    return new PdfName(token.Value);

                case PdfTokenType.String:
                    return new PdfString(token.Value, false);

                case PdfTokenType.HexString:
                    return new PdfString(token.Value, true);

                case PdfTokenType.Boolean:
                    return new PdfBoolean(token.Value == "true");

                case PdfTokenType.Null:
                    return PdfNull.Instance;

                case PdfTokenType.ArrayStart:
                {
                    var array = new PdfArray();
                    while (true)
                    {
                        tokenizer.SkipWhitespaceAndComments();
                        if (tokenizer.Position < tokenizer.Length && tokenizer.PeekByte() == ']')
                        {
                            tokenizer.NextToken(); // consume ']'
                            break;
                        }
                        if (tokenizer.Position >= tokenizer.Length)
                            break;
                        array.Items.Add(ParseObjectWith(tokenizer));
                    }
                    return array;
                }

                case PdfTokenType.DictStart:
                {
                    var dict = new PdfDictionary();
                    while (true)
                    {
                        tokenizer.SkipWhitespaceAndComments();
                        if (tokenizer.Position >= tokenizer.Length)
                            break;
                        // Check for >>
                        if (tokenizer.PeekByte() == '>')
                        {
                            tokenizer.NextToken(); // consume '>>'
                            break;
                        }
                        var keyToken = tokenizer.NextToken();
                        if (keyToken.Type == PdfTokenType.DictEnd)
                            break;
                        if (keyToken.Type != PdfTokenType.Name)
                            throw new InvalidOperationException($"Expected name key in dictionary, got: {keyToken}");
                        var value = ParseObjectWith(tokenizer);
                        dict.Entries[keyToken.Value] = value;
                    }
                    return dict;
                }

                case PdfTokenType.Keyword:
                    // Return as-is for keywords like "endobj"
                    return new PdfName(token.Value);

                default:
                    return PdfNull.Instance;
            }
        }

        private byte[] ReadStreamData(PdfDictionary dict)
        {
            // After parsing the dictionary, skip to "stream" keyword
            // The stream keyword is followed by \r\n or \n, then the raw data
            SkipToStreamData();

            // Determine length
            int length = 0;
            var lengthObj = dict.Get("Length");
            if (lengthObj is PdfNumber num)
            {
                length = num.IntValue;
            }
            else if (lengthObj is PdfReference lengthRef)
            {
                var resolved = ReadObject(lengthRef.ObjectNumber);
                if (resolved is PdfNumber resolvedNum)
                    length = resolvedNum.IntValue;
            }

            if (length <= 0 || _tokenizer.Position + length > _data.Length)
            {
                // Try to find endstream
                length = FindEndStream(_tokenizer.Position);
            }

            byte[] result = new byte[length];
            Array.Copy(_data, _tokenizer.Position, result, 0, length);
            _tokenizer.Position += length;
            return result;
        }

        private byte[] ReadStreamDataDirect(PdfDictionary dict)
        {
            // Tokenizer is already positioned right after "stream\n"
            int length = 0;
            var lengthObj = dict.Get("Length");
            if (lengthObj is PdfNumber num)
            {
                length = num.IntValue;
            }
            else if (lengthObj is PdfReference lengthRef)
            {
                int savedPos = _tokenizer.Position;
                var resolved = ReadObject(lengthRef.ObjectNumber);
                _tokenizer.Position = savedPos;
                if (resolved is PdfNumber resolvedNum)
                    length = resolvedNum.IntValue;
            }

            if (length <= 0 || _tokenizer.Position + length > _data.Length)
            {
                length = FindEndStream(_tokenizer.Position);
            }

            byte[] result = new byte[length];
            Array.Copy(_data, _tokenizer.Position, result, 0, length);
            _tokenizer.Position += length;
            return result;
        }

        private void SkipToStreamData()
        {
            // Position should be right after the dictionary parsing
            // We need to find "stream" and skip past the EOL
            while (_tokenizer.Position < _data.Length)
            {
                if (_data[_tokenizer.Position] == 's')
                {
                    // Check for "stream"
                    if (_tokenizer.Position + 6 <= _data.Length &&
                        _data[_tokenizer.Position + 1] == 't' &&
                        _data[_tokenizer.Position + 2] == 'r' &&
                        _data[_tokenizer.Position + 3] == 'e' &&
                        _data[_tokenizer.Position + 4] == 'a' &&
                        _data[_tokenizer.Position + 5] == 'm')
                    {
                        _tokenizer.Position += 6;
                        // Skip EOL after "stream"
                        if (_tokenizer.Position < _data.Length && _data[_tokenizer.Position] == '\r')
                            _tokenizer.Position++;
                        if (_tokenizer.Position < _data.Length && _data[_tokenizer.Position] == '\n')
                            _tokenizer.Position++;
                        return;
                    }
                }
                _tokenizer.Position++;
            }
        }

        private int FindEndStream(int startPos)
        {
            var pattern = Encoding.ASCII.GetBytes("endstream");
            for (int i = startPos; i < _data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (_data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    int len = i - startPos;
                    // Trim trailing whitespace
                    while (len > 0 && (_data[startPos + len - 1] == '\r' || _data[startPos + len - 1] == '\n'))
                        len--;
                    return len;
                }
            }
            return 0;
        }

        public static byte[] DecompressStream(PdfDictionary dict, byte[] rawData)
        {
            var filter = dict.Get("Filter");
            if (filter == null) return rawData;

            List<string> filters = new List<string>();
            if (filter is PdfName name)
            {
                filters.Add(name.Name);
            }
            else if (filter is PdfArray arr)
            {
                foreach (var item in arr.Items)
                {
                    if (item is PdfName n) filters.Add(n.Name);
                }
            }

            byte[] result = rawData;
            foreach (var f in filters)
            {
                switch (f)
                {
                    case "FlateDecode":
                        result = FlateDecompress(result);
                        break;
                    case "ASCIIHexDecode":
                        result = AsciiHexDecode(result);
                        break;
                    default:
                        throw new NotSupportedException($"PDF filter not supported: {f}");
                }
            }

            // Apply predictor de-filtering if specified in DecodeParms
            result = ApplyPredictor(dict, result);

            return result;
        }

        private static byte[] ApplyPredictor(PdfDictionary dict, byte[] data)
        {
            var decodeParms = dict.Get("DecodeParms") as PdfDictionary;
            if (decodeParms == null)
            {
                // DecodeParms might be in an array (one per filter)
                var decodeParmsArray = dict.Get("DecodeParms") as PdfArray;
                if (decodeParmsArray != null && decodeParmsArray.Items.Count > 0)
                    decodeParms = decodeParmsArray.Items[0] as PdfDictionary;
            }
            if (decodeParms == null) return data;

            var predictorObj = decodeParms.Get("Predictor") as PdfNumber;
            int predictor = predictorObj?.IntValue ?? 1;

            // Predictor 1 = no prediction, nothing to do
            if (predictor == 1) return data;

            var columnsObj = decodeParms.Get("Columns") as PdfNumber;
            int columns = columnsObj?.IntValue ?? 1;

            // TIFF Predictor 2
            if (predictor == 2)
            {
                return ApplyTiffPredictor(data, columns);
            }

            // PNG Predictors (10-15)
            if (predictor >= 10 && predictor <= 15)
            {
                return ApplyPngPredictor(data, columns);
            }

            return data;
        }

        private static byte[] ApplyPngPredictor(byte[] data, int columns)
        {
            // Each row: 1 filter byte + columns data bytes
            int rowSize = columns + 1;
            if (data.Length % rowSize != 0 && data.Length % (columns + 1) != 0)
            {
                // Try to auto-detect: some PDFs don't include the filter byte
                // If data divides evenly by columns but not by columns+1, assume no filter bytes
                if (data.Length % columns == 0)
                    return data;
            }

            int numRows = data.Length / rowSize;
            byte[] output = new byte[numRows * columns];
            byte[] prevRow = new byte[columns];

            for (int row = 0; row < numRows; row++)
            {
                int srcOffset = row * rowSize;
                int dstOffset = row * columns;
                byte filterType = data[srcOffset];

                switch (filterType)
                {
                    case 0: // None
                        Array.Copy(data, srcOffset + 1, output, dstOffset, columns);
                        break;

                    case 1: // Sub
                        for (int j = 0; j < columns; j++)
                        {
                            byte left = (j > 0) ? output[dstOffset + j - 1] : (byte)0;
                            output[dstOffset + j] = (byte)(data[srcOffset + 1 + j] + left);
                        }
                        break;

                    case 2: // Up
                        for (int j = 0; j < columns; j++)
                        {
                            output[dstOffset + j] = (byte)(data[srcOffset + 1 + j] + prevRow[j]);
                        }
                        break;

                    case 3: // Average
                        for (int j = 0; j < columns; j++)
                        {
                            byte left = (j > 0) ? output[dstOffset + j - 1] : (byte)0;
                            int avg = ((int)left + (int)prevRow[j]) / 2;
                            output[dstOffset + j] = (byte)(data[srcOffset + 1 + j] + avg);
                        }
                        break;

                    case 4: // Paeth
                        for (int j = 0; j < columns; j++)
                        {
                            byte left = (j > 0) ? output[dstOffset + j - 1] : (byte)0;
                            byte up = prevRow[j];
                            byte upLeft = (j > 0) ? prevRow[j - 1] : (byte)0;
                            output[dstOffset + j] = (byte)(data[srcOffset + 1 + j] + PaethPredictor(left, up, upLeft));
                        }
                        break;

                    default:
                        // Unknown filter, just copy raw
                        Array.Copy(data, srcOffset + 1, output, dstOffset, columns);
                        break;
                }

                // Save current row as previous for next iteration
                Array.Copy(output, dstOffset, prevRow, 0, columns);
            }

            return output;
        }

        private static byte PaethPredictor(byte a, byte b, byte c)
        {
            int p = (int)a + (int)b - (int)c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        private static byte[] ApplyTiffPredictor(byte[] data, int columns)
        {
            // TIFF Predictor 2: each byte is delta from the previous in the same row
            if (columns <= 0) return data;
            int numRows = data.Length / columns;
            byte[] output = new byte[data.Length];

            for (int row = 0; row < numRows; row++)
            {
                int offset = row * columns;
                output[offset] = data[offset];
                for (int j = 1; j < columns; j++)
                {
                    output[offset + j] = (byte)(data[offset + j] + output[offset + j - 1]);
                }
            }
            return output;
        }

        private static byte[] FlateDecompress(byte[] data)
        {
            // Determine if data has a zlib header
            int offset = 0;
            int length = data.Length;
            if (data.Length >= 2)
            {
                int cmf = data[0];
                int flg = data[1];
                if ((cmf & 0x0F) == 8 && (cmf * 256 + flg) % 31 == 0)
                {
                    // Skip 2-byte zlib header; also skip 4-byte Adler-32 checksum at end
                    offset = 2;
                    length = data.Length - 2;
                    // Check for FDICT flag (bit 5 of FLG)
                    if ((flg & 0x20) != 0)
                    {
                        offset += 4; // skip DICTID
                        length -= 4;
                    }
                }
            }

            // Try with calculated offset first, then without, then with offset 0
            int[] offsets = offset > 0 ? new[] { offset, 0 } : new[] { 0, 2 };
            Exception lastEx = null;

            foreach (int off in offsets)
            {
                try
                {
                    int len = data.Length - off;
                    if (len <= 0) continue;
                    using (var input = new MemoryStream(data, off, len))
                    using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                    using (var output = new MemoryStream())
                    {
                        deflate.CopyTo(output);
                        return output.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }

            throw lastEx ?? new InvalidOperationException("Failed to decompress FlateDecode stream");
        }

        public static byte[] FlateCompress(byte[] data)
        {
            // PDF spec (ISO 32000-1 §7.4.4) requires zlib format (RFC 1950)
            // for FlateDecode: 2-byte header + deflate data + 4-byte Adler-32
            using (var output = new MemoryStream())
            {
                // Zlib header: CMF=0x78 (deflate, 32K window), FLG=0x9C (level 2, no dict, check bits)
                output.WriteByte(0x78);
                output.WriteByte(0x9C);

                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(data, 0, data.Length);
                }

                // Adler-32 checksum (big-endian)
                uint a = 1, b = 0;
                foreach (byte d in data)
                {
                    a = (a + d) % 65521;
                    b = (b + a) % 65521;
                }
                uint adler = (b << 16) | a;
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)(adler));

                return output.ToArray();
            }
        }


        private static byte[] AsciiHexDecode(byte[] data)
        {
            var output = new MemoryStream();
            int i = 0;
            while (i < data.Length)
            {
                byte c1 = data[i++];
                if (c1 == '>') break;
                if (PdfTokenizer.IsWhitespace(c1)) continue;

                byte c2 = (i < data.Length) ? data[i++] : (byte)'0';
                while (PdfTokenizer.IsWhitespace(c2) && i < data.Length)
                    c2 = data[i++];

                int hi = HexVal(c1);
                int lo = HexVal(c2);
                output.WriteByte((byte)(hi * 16 + lo));
            }
            return output.ToArray();
        }

        private static int HexVal(byte b)
        {
            if (b >= '0' && b <= '9') return b - '0';
            if (b >= 'a' && b <= 'f') return b - 'a' + 10;
            if (b >= 'A' && b <= 'F') return b - 'A' + 10;
            return 0;
        }

        // Navigate to get the XFA object following the path: Catalog → AcroForm → XFA
        public (PdfObject xfaObj, PdfDictionary acroForm, int acroFormObjNum, PdfDictionary catalog, int catalogObjNum) GetXfaObject()
        {
            // Get catalog
            var rootRef = _trailer != null ? _trailer.Get("Root") as PdfReference : null;
            if (rootRef == null)
                throw new InvalidOperationException("No Root in trailer");

            int catalogObjNum = rootRef.ObjectNumber;
            var catalog = ReadObject(rootRef.ObjectNumber);
            if (catalog is PdfStream catStream)
                catalog = catStream.Dictionary;
            var catalogDict = catalog as PdfDictionary;
            if (catalogDict == null)
                throw new InvalidOperationException("Catalog is not a dictionary");

            // Get AcroForm
            var acroFormRef = catalogDict.Get("AcroForm");
            if (acroFormRef == null)
                throw new InvalidOperationException("No AcroForm in catalog - this PDF has no form");

            int acroFormObjNum = -1;
            PdfDictionary acroFormDict;
            if (acroFormRef is PdfReference afRef)
            {
                acroFormObjNum = afRef.ObjectNumber;
                var resolved = ReadObject(afRef.ObjectNumber);
                acroFormDict = resolved as PdfDictionary;
                if (resolved is PdfStream s)
                    acroFormDict = s.Dictionary;
            }
            else
            {
                acroFormDict = acroFormRef as PdfDictionary;
            }

            if (acroFormDict == null)
                throw new InvalidOperationException("AcroForm is not a dictionary");

            // Get XFA
            var xfa = acroFormDict.Get("XFA");
            if (xfa == null)
                throw new InvalidOperationException("No XFA entry in AcroForm - this is not an XFA form");

            return (xfa, acroFormDict, acroFormObjNum, catalogDict, catalogObjNum);
        }

        // Get decompressed stream bytes
        public byte[] GetDecompressedStreamBytes(PdfStream stream)
        {
            return DecompressStream(stream.Dictionary, stream.RawData);
        }

        public int NextObjectNumber
        {
            get
            {
                var size = _trailer != null ? _trailer.Get("Size") as PdfNumber : null;
                return size?.IntValue ?? (_xref.Count > 0 ? MaxObjectNumber + 1 : 1);
            }
        }

        public int MaxObjectNumber
        {
            get
            {
                int max = 0;
                foreach (var kvp in _xref)
                    if (kvp.Key > max) max = kvp.Key;
                return max;
            }
        }

        public byte[] RawData => _data;
    }
}
