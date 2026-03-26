using System;
using System.Collections.Generic;
using System.Text;

namespace XfaPdfFiller
{
    internal enum PdfTokenType
    {
        Number,
        Name,
        String,
        HexString,
        Boolean,
        Null,
        ArrayStart,
        ArrayEnd,
        DictStart,
        DictEnd,
        Keyword,
        Eof
    }

    internal struct PdfToken
    {
        public PdfTokenType Type;
        public string Value;
        public long Position;

        public PdfToken(PdfTokenType type, string value, long position)
        {
            Type = type;
            Value = value;
            Position = position;
        }

        public override string ToString() => $"{Type}: {Value}";
    }

    internal abstract class PdfObject
    {
        public abstract void WriteTo(PdfSerializationContext ctx);
    }

    internal class PdfNumber : PdfObject
    {
        public double Value { get; }
        public bool IsInteger => Value == Math.Floor(Value) && !double.IsInfinity(Value);

        public PdfNumber(double value) => Value = value;
        public int IntValue => (int)Value;
        public long LongValue => (long)Value;

        public override void WriteTo(PdfSerializationContext ctx)
        {
            if (IsInteger)
                ctx.Write(LongValue.ToString());
            else
                ctx.Write(Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    internal class PdfName : PdfObject
    {
        public string Name { get; }
        public PdfName(string name) => Name = name;

        public override void WriteTo(PdfSerializationContext ctx)
        {
            ctx.Write("/");
            foreach (char c in Name)
            {
                if (c < 0x21 || c > 0x7E || c == '#' || c == '/' || c == '(' || c == ')' ||
                    c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '%')
                {
                    ctx.Write($"#{(int)c:X2}");
                }
                else
                {
                    ctx.Write(c.ToString());
                }
            }
        }

        public override bool Equals(object? obj) => obj is PdfName other && Name == other.Name;
        public override int GetHashCode() => Name.GetHashCode();
    }

    internal class PdfString : PdfObject
    {
        public string Value { get; }
        public bool IsHex { get; }

        public PdfString(string value, bool isHex = false)
        {
            Value = value;
            IsHex = isHex;
        }

        public override void WriteTo(PdfSerializationContext ctx)
        {
            if (IsHex)
            {
                ctx.Write("<");
                foreach (char c in Value)
                    ctx.Write(((int)c).ToString("X2"));
                ctx.Write(">");
            }
            else
            {
                ctx.Write("(");
                foreach (char c in Value)
                {
                    switch (c)
                    {
                        case '\\': ctx.Write("\\\\"); break;
                        case '(': ctx.Write("\\("); break;
                        case ')': ctx.Write("\\)"); break;
                        case '\r': ctx.Write("\\r"); break;
                        case '\n': ctx.Write("\\n"); break;
                        default: ctx.Write(c.ToString()); break;
                    }
                }
                ctx.Write(")");
            }
        }
    }

    internal class PdfBoolean : PdfObject
    {
        public bool Value { get; }
        public PdfBoolean(bool value) => Value = value;

        public override void WriteTo(PdfSerializationContext ctx)
        {
            ctx.Write(Value ? "true" : "false");
        }
    }

    internal class PdfNull : PdfObject
    {
        public static readonly PdfNull Instance = new PdfNull();

        public override void WriteTo(PdfSerializationContext ctx)
        {
            ctx.Write("null");
        }
    }

    internal class PdfArray : PdfObject
    {
        public List<PdfObject> Items { get; } = new List<PdfObject>();

        public override void WriteTo(PdfSerializationContext ctx)
        {
            ctx.Write("[");
            for (int i = 0; i < Items.Count; i++)
            {
                if (i > 0) ctx.Write(" ");
                Items[i].WriteTo(ctx);
            }
            ctx.Write("]");
        }
    }

    internal class PdfDictionary : PdfObject
    {
        public Dictionary<string, PdfObject> Entries { get; } = new Dictionary<string, PdfObject>();

        public PdfObject? Get(string name) =>
            Entries.TryGetValue(name, out var val) ? val : null;

        public override void WriteTo(PdfSerializationContext ctx)
        {
            ctx.Write("<<");
            foreach (var kvp in Entries)
            {
                ctx.Write(" ");
                new PdfName(kvp.Key).WriteTo(ctx);
                ctx.Write(" ");
                kvp.Value.WriteTo(ctx);
            }
            ctx.Write(" >>");
        }
    }

    internal class PdfReference : PdfObject
    {
        public int ObjectNumber { get; }
        public int Generation { get; }

        public PdfReference(int objNum, int gen)
        {
            ObjectNumber = objNum;
            Generation = gen;
        }

        public override void WriteTo(PdfSerializationContext ctx)
        {
            ctx.Write($"{ObjectNumber} {Generation} R");
        }

        public override bool Equals(object? obj) =>
            obj is PdfReference other && ObjectNumber == other.ObjectNumber && Generation == other.Generation;

        public override int GetHashCode() => ObjectNumber * 397 ^ Generation;
    }

    internal class PdfStream : PdfObject
    {
        public PdfDictionary Dictionary { get; }
        public byte[] RawData { get; set; }

        public PdfStream(PdfDictionary dict, byte[] rawData)
        {
            Dictionary = dict;
            RawData = rawData;
        }

        public override void WriteTo(PdfSerializationContext ctx)
        {
            Dictionary.Entries["Length"] = new PdfNumber(RawData.Length);
            Dictionary.WriteTo(ctx);
            ctx.Write("\nstream\n");
            ctx.WriteBytes(RawData);
            ctx.Write("\nendstream");
        }
    }

    internal class PdfSerializationContext
    {
        private readonly System.IO.Stream _stream;
        private readonly Encoding _encoding = new System.Text.UTF8Encoding(false);

        public long Position => _stream.Position;

        public PdfSerializationContext(System.IO.Stream stream) => _stream = stream;

        public void Write(string text)
        {
            var bytes = _encoding.GetBytes(text);
            _stream.Write(bytes, 0, bytes.Length);
        }

        public void WriteBytes(byte[] data)
        {
            _stream.Write(data, 0, data.Length);
        }

        public void WriteByte(byte b)
        {
            _stream.WriteByte(b);
        }
    }

    internal struct XrefEntry
    {
        public long Offset;
        public int Generation;
        public bool InUse;
        public int ObjectNumber;

        // For xref streams: object stream number and index
        public int StreamObjectNumber;
        public int IndexInStream;
        public bool IsCompressed;
    }
}
