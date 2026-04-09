// ============================================================================
//  XfaPdfFiller - Biblioteca para rellenar formularios XFA en documentos PDF
//  Reemplazo de iTextSharp sin dependencias externas
// ----------------------------------------------------------------------------
//  09/04/2026 PHP:
//  - Permite inyectar datos XML en formularios PDF con estructura XFA,
//    parseando y reescribiendo el binario PDF mediante actualizacion incremental.
//  - Este fichero implementa el escritor incremental de PDF. Copia el documento
//    original y añade al final los objetos nuevos o modificados, generando una
//    tabla de referencias cruzadas en formato xref stream (PDF 1.5+) con
//    compresion FlateDecode, preservando la firma de Reader Extensions.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XfaPdfFiller
{
    internal class PdfIncrementalWriter
    {
        private readonly MemoryStream _output;
        private readonly PdfDocumentReader _reader;
        private readonly int _startObjectNumber;
        private int _nextObjectNumber;
        private readonly List<(int objNum, int gen, long offset)> _newObjects = new List<(int, int, long)>();

        public PdfIncrementalWriter(PdfDocumentReader reader, MemoryStream output)
        {
            _reader = reader;
            _output = output;
            _startObjectNumber = reader.NextObjectNumber;
            _nextObjectNumber = _startObjectNumber;

            // Copy original PDF data
            var rawData = reader.RawData;
            output.Write(rawData, 0, rawData.Length);
        }

        public int AllocateObjectNumber() => _nextObjectNumber++;

        public PdfReference WriteNewObject(PdfObject obj)
        {
            int objNum = AllocateObjectNumber();
            WriteObject(objNum, 0, obj);
            return new PdfReference(objNum, 0);
        }

        public void WriteReplacementObject(int objNum, int gen, PdfObject obj)
        {
            WriteObject(objNum, gen, obj);
        }

        private void WriteObject(int objNum, int gen, PdfObject obj)
        {
            long offset = _output.Position;
            var ctx = new PdfSerializationContext(_output);

            ctx.Write($"{objNum} {gen} obj\n");
            obj.WriteTo(ctx);
            ctx.Write("\nendobj\n");

            _newObjects.Add((objNum, gen, offset));
        }

        public byte[] Finish()
        {
            // Find the previous startxref value
            long prevXrefOffset = 0;
            var startxrefPattern = Encoding.ASCII.GetBytes("startxref");
            var tokenizer = new PdfTokenizer(_reader.RawData);
            int prevXrefPos = tokenizer.FindBackward(startxrefPattern, -1);
            if (prevXrefPos >= 0)
            {
                tokenizer.Position = prevXrefPos + startxrefPattern.Length;
                tokenizer.SkipWhitespaceAndComments();
                var tok = tokenizer.NextToken();
                prevXrefOffset = long.Parse(tok.Value);
            }

            // Use the xref stream object number
            int xrefObjNum = _nextObjectNumber++;

            // Build xref stream data
            // W = [1 3 2]: type(1 byte), offset(3 bytes), gen(2 bytes)
            int w0 = 1, w1 = 3, w2 = 2;
            int entrySize = w0 + w1 + w2;

            // Build Index array and entry data
            // Include free head entry (obj 0) + all our modified/new objects
            var sortedObjects = new List<(int objNum, int gen, long offset)>(_newObjects);
            sortedObjects.Sort((a, b) => a.objNum.CompareTo(b.objNum));

            // Build subsections: [obj0: free head] + [our objects in contiguous groups]
            var indexEntries = new List<int>();
            var xrefData = new MemoryStream();

            // Object 0: free head entry (type=0, next free=0, gen=65535)
            indexEntries.Add(0);
            indexEntries.Add(1);
            WriteByte(xrefData, 0);                     // type 0 = free
            WriteBytes(xrefData, 0, w1);                // next free obj = 0
            WriteBytes(xrefData, 65535, w2);             // gen 65535

            // Group our objects into contiguous ranges
            int i = 0;
            while (i < sortedObjects.Count)
            {
                int start = sortedObjects[i].objNum;
                int rangeStart = i;
                while (i + 1 < sortedObjects.Count && sortedObjects[i + 1].objNum == sortedObjects[i].objNum + 1)
                    i++;
                int count = i - rangeStart + 1;

                indexEntries.Add(start);
                indexEntries.Add(count);

                for (int j = rangeStart; j <= i; j++)
                {
                    WriteByte(xrefData, 1);                                  // type 1 = regular object
                    WriteBytes(xrefData, (int)sortedObjects[j].offset, w1);  // offset
                    WriteBytes(xrefData, sortedObjects[j].gen, w2);          // generation
                }
                i++;
            }

            // Add the xref stream object itself
            long xrefStreamOffset = _output.Position + 0; // will be set precisely below

            // Add xref stream's own entry to the index
            indexEntries.Add(xrefObjNum);
            indexEntries.Add(1);
            // Placeholder - we need to know the offset, which is where we're about to write
            // We'll calculate it after building the dictionary

            byte[] xrefRawData = xrefData.ToArray();

            // Build the xref stream dictionary
            var xrefDict = new PdfDictionary();
            xrefDict.Entries["Type"] = new PdfName("XRef");

            // W array
            var wArray = new PdfArray();
            wArray.Items.Add(new PdfNumber(w0));
            wArray.Items.Add(new PdfNumber(w1));
            wArray.Items.Add(new PdfNumber(w2));
            xrefDict.Entries["W"] = wArray;

            // Index array
            var indexArray = new PdfArray();
            foreach (var idx in indexEntries)
                indexArray.Items.Add(new PdfNumber(idx));
            xrefDict.Entries["Index"] = indexArray;

            xrefDict.Entries["Size"] = new PdfNumber(_nextObjectNumber);
            xrefDict.Entries["Prev"] = new PdfNumber(prevXrefOffset);

            // Copy essential trailer entries
            var origTrailer = _reader.Trailer;
            foreach (var key in new[] { "Root", "Info", "ID", "Encrypt" })
            {
                var val = origTrailer.Get(key);
                if (val != null)
                    xrefDict.Entries[key] = val;
            }

            // Compress xref data with FlateDecode
            // We need to add the xref stream's own entry to the raw data first
            // The xref stream itself is type=1 at the offset we're about to write
            // We'll write it, then fix up

            // Calculate where the xref stream object will start
            // First, let's build everything except the stream data, to know the offset
            xrefStreamOffset = _output.Position;

            // Add the xref stream's own entry data
            var selfEntry = new MemoryStream();
            WriteByte(selfEntry, 1);                              // type 1
            WriteBytes(selfEntry, (int)xrefStreamOffset, w1);     // offset
            WriteBytes(selfEntry, 0, w2);                         // gen 0
            byte[] selfEntryBytes = selfEntry.ToArray();

            // Combine all xref data
            byte[] fullXrefData = new byte[xrefRawData.Length + selfEntryBytes.Length];
            Array.Copy(xrefRawData, 0, fullXrefData, 0, xrefRawData.Length);
            Array.Copy(selfEntryBytes, 0, fullXrefData, xrefRawData.Length, selfEntryBytes.Length);

            // Compress
            byte[] compressedXref = PdfDocumentReader.FlateCompress(fullXrefData);

            xrefDict.Entries["Length"] = new PdfNumber(compressedXref.Length);
            xrefDict.Entries["Filter"] = new PdfName("FlateDecode");

            // Write the xref stream as an object
            var stream = new PdfStream(xrefDict, compressedXref);
            var ctx = new PdfSerializationContext(_output);
            ctx.Write($"{xrefObjNum} 0 obj\n");
            stream.WriteTo(ctx);
            ctx.Write("\nendobj\n");

            ctx.Write($"startxref\n{xrefStreamOffset}\n%%EOF\n");

            return _output.ToArray();
        }

        private static void WriteByte(MemoryStream ms, int value)
        {
            ms.WriteByte((byte)(value & 0xFF));
        }

        private static void WriteBytes(MemoryStream ms, int value, int width)
        {
            for (int i = width - 1; i >= 0; i--)
            {
                ms.WriteByte((byte)((value >> (i * 8)) & 0xFF));
            }
        }
    }
}
