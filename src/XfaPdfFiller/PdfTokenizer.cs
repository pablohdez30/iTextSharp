// ============================================================================
//  XfaPdfFiller - Biblioteca para rellenar formularios XFA en documentos PDF
//  Reemplazo de iTextSharp sin dependencias externas
// ----------------------------------------------------------------------------
//  09/04/2026 PHP:
//  - Permite inyectar datos XML en formularios PDF con estructura XFA,
//    parseando y reescribiendo el binario PDF mediante actualizacion incremental.
//  - Este fichero implementa el tokenizador de bajo nivel para documentos PDF.
//    Lee el flujo de bytes crudo y lo descompone en tokens (nombres, cadenas,
//    numeros, delimitadores, palabras clave) gestionando comentarios, cadenas
//    hexadecimales, secuencias de escape y busqueda hacia atras en el buffer.
// ============================================================================

using System;
using System.IO;
using System.Text;

namespace XfaPdfFiller
{
    internal class PdfTokenizer
    {
        private readonly byte[] _data;
        private int _pos;

        public int Position
        {
            get => _pos;
            set => _pos = value;
        }

        public int Length => _data.Length;

        public PdfTokenizer(byte[] data)
        {
            _data = data;
            _pos = 0;
        }

        public byte[] Data => _data;

        public byte PeekByte() => _pos < _data.Length ? _data[_pos] : (byte)0;

        public byte ReadByte() => _pos < _data.Length ? _data[_pos++] : (byte)0;

        public void SkipWhitespaceAndComments()
        {
            while (_pos < _data.Length)
            {
                byte b = _data[_pos];
                if (b == '%')
                {
                    // Skip comment until end of line
                    while (_pos < _data.Length && _data[_pos] != '\n' && _data[_pos] != '\r')
                        _pos++;
                    continue;
                }
                if (IsWhitespace(b))
                {
                    _pos++;
                    continue;
                }
                break;
            }
        }

        public PdfToken NextToken()
        {
            SkipWhitespaceAndComments();

            if (_pos >= _data.Length)
                return new PdfToken(PdfTokenType.Eof, "", _pos);

            long startPos = _pos;
            byte b = _data[_pos];

            // Name
            if (b == '/')
            {
                _pos++;
                var sb = new StringBuilder();
                while (_pos < _data.Length && !IsDelimiter(_data[_pos]) && !IsWhitespace(_data[_pos]))
                {
                    if (_data[_pos] == '#' && _pos + 2 < _data.Length)
                    {
                        _pos++;
                        int hi = HexVal(_data[_pos++]);
                        int lo = HexVal(_data[_pos++]);
                        sb.Append((char)(hi * 16 + lo));
                    }
                    else
                    {
                        sb.Append((char)_data[_pos++]);
                    }
                }
                return new PdfToken(PdfTokenType.Name, sb.ToString(), startPos);
            }

            // String literal
            if (b == '(')
            {
                _pos++;
                var sb = new StringBuilder();
                int depth = 1;
                while (_pos < _data.Length && depth > 0)
                {
                    byte c = _data[_pos++];
                    if (c == '\\' && _pos < _data.Length)
                    {
                        byte next = _data[_pos++];
                        switch (next)
                        {
                            case (byte)'n': sb.Append('\n'); break;
                            case (byte)'r': sb.Append('\r'); break;
                            case (byte)'t': sb.Append('\t'); break;
                            case (byte)'b': sb.Append('\b'); break;
                            case (byte)'f': sb.Append('\f'); break;
                            case (byte)'(': sb.Append('('); break;
                            case (byte)')': sb.Append(')'); break;
                            case (byte)'\\': sb.Append('\\'); break;
                            default:
                                if (next >= '0' && next <= '7')
                                {
                                    int octal = next - '0';
                                    if (_pos < _data.Length && _data[_pos] >= '0' && _data[_pos] <= '7')
                                    {
                                        octal = octal * 8 + (_data[_pos++] - '0');
                                        if (_pos < _data.Length && _data[_pos] >= '0' && _data[_pos] <= '7')
                                            octal = octal * 8 + (_data[_pos++] - '0');
                                    }
                                    sb.Append((char)octal);
                                }
                                else
                                {
                                    sb.Append((char)next);
                                }
                                break;
                        }
                    }
                    else if (c == '(')
                    {
                        depth++;
                        sb.Append('(');
                    }
                    else if (c == ')')
                    {
                        depth--;
                        if (depth > 0) sb.Append(')');
                    }
                    else
                    {
                        sb.Append((char)c);
                    }
                }
                return new PdfToken(PdfTokenType.String, sb.ToString(), startPos);
            }

            // Hex string or dictionary start
            if (b == '<')
            {
                _pos++;
                if (_pos < _data.Length && _data[_pos] == '<')
                {
                    _pos++;
                    return new PdfToken(PdfTokenType.DictStart, "<<", startPos);
                }
                // Hex string
                var sb = new StringBuilder();
                while (_pos < _data.Length && _data[_pos] != '>')
                {
                    if (!IsWhitespace(_data[_pos]))
                        sb.Append((char)_data[_pos]);
                    _pos++;
                }
                if (_pos < _data.Length) _pos++; // skip '>'
                string hex = sb.ToString();
                if (hex.Length % 2 == 1) hex += "0";
                var result = new StringBuilder();
                for (int i = 0; i < hex.Length; i += 2)
                    result.Append((char)(HexVal((byte)hex[i]) * 16 + HexVal((byte)hex[i + 1])));
                return new PdfToken(PdfTokenType.HexString, result.ToString(), startPos);
            }

            // Dictionary end
            if (b == '>' && _pos + 1 < _data.Length && _data[_pos + 1] == '>')
            {
                _pos += 2;
                return new PdfToken(PdfTokenType.DictEnd, ">>", startPos);
            }

            // Array
            if (b == '[')
            {
                _pos++;
                return new PdfToken(PdfTokenType.ArrayStart, "[", startPos);
            }
            if (b == ']')
            {
                _pos++;
                return new PdfToken(PdfTokenType.ArrayEnd, "]", startPos);
            }

            // Number (including negative)
            if (b == '+' || b == '-' || b == '.' || (b >= '0' && b <= '9'))
            {
                var sb = new StringBuilder();
                sb.Append((char)_data[_pos++]);
                while (_pos < _data.Length &&
                       (_data[_pos] == '.' || (_data[_pos] >= '0' && _data[_pos] <= '9')))
                {
                    sb.Append((char)_data[_pos++]);
                }
                return new PdfToken(PdfTokenType.Number, sb.ToString(), startPos);
            }

            // Keyword (true, false, null, obj, endobj, stream, endstream, R, etc.)
            {
                var sb = new StringBuilder();
                while (_pos < _data.Length && !IsWhitespace(_data[_pos]) && !IsDelimiter(_data[_pos]))
                {
                    sb.Append((char)_data[_pos++]);
                }
                string word = sb.ToString();
                switch (word)
                {
                    case "true": return new PdfToken(PdfTokenType.Boolean, "true", startPos);
                    case "false": return new PdfToken(PdfTokenType.Boolean, "false", startPos);
                    case "null": return new PdfToken(PdfTokenType.Null, "null", startPos);
                    default: return new PdfToken(PdfTokenType.Keyword, word, startPos);
                }
            }
        }

        public static bool IsWhitespace(byte b) =>
            b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;

        public static bool IsDelimiter(byte b) =>
            b == '(' || b == ')' || b == '<' || b == '>' || b == '[' || b == ']' ||
            b == '{' || b == '}' || b == '/' || b == '%';

        private static int HexVal(byte b)
        {
            if (b >= '0' && b <= '9') return b - '0';
            if (b >= 'a' && b <= 'f') return b - 'a' + 10;
            if (b >= 'A' && b <= 'F') return b - 'A' + 10;
            return 0;
        }

        // Read bytes from a specific position
        public byte[] ReadBytes(long offset, int length)
        {
            var result = new byte[length];
            Array.Copy(_data, offset, result, 0, Math.Min(length, _data.Length - (int)offset));
            return result;
        }

        // Find a byte sequence searching backwards from a position
        public int FindBackward(byte[] pattern, int fromPos)
        {
            if (fromPos < 0) fromPos = _data.Length - 1;
            for (int i = Math.Min(fromPos, _data.Length - pattern.Length); i >= 0; i--)
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
                if (match) return i;
            }
            return -1;
        }
    }
}
