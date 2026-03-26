using System;
using System.Collections.Generic;
using System.IO;
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
        private readonly Dictionary<int, long> _replacedObjects = new Dictionary<int, long>();

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
            _replacedObjects[objNum] = _newObjects[_newObjects.Count - 1].offset;
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
            var ctx = new PdfSerializationContext(_output);
            long xrefOffset = _output.Position;

            // Write xref table
            ctx.Write("xref\n");

            // Group objects into contiguous ranges
            var allObjects = new List<(int objNum, int gen, long offset)>(_newObjects);
            allObjects.Sort((a, b) => a.objNum.CompareTo(b.objNum));

            // Write subsections
            int i = 0;
            while (i < allObjects.Count)
            {
                int start = allObjects[i].objNum;
                int end = start;
                while (i + 1 < allObjects.Count && allObjects[i + 1].objNum == end + 1)
                {
                    end++;
                    i++;
                }

                int count = end - start + 1;
                ctx.Write($"{start} {count}\n");

                for (int j = 0; j < count; j++)
                {
                    var entry = allObjects[i - count + 1 + j];
                    ctx.Write($"{entry.offset:D10} {entry.gen:D5} n \n");
                }
                i++;
            }

            // Write trailer
            ctx.Write("trailer\n");
            var trailer = new PdfDictionary();

            // Copy essential entries from original trailer
            var origTrailer = _reader.Trailer;
            foreach (var key in new[] { "Root", "Info", "ID", "Encrypt" })
            {
                var val = origTrailer.Get(key);
                if (val != null)
                    trailer.Entries[key] = val;
            }

            trailer.Entries["Size"] = new PdfNumber(_nextObjectNumber);
            // Prev points to the previous xref
            var startxrefPattern = Encoding.ASCII.GetBytes("startxref");
            var tokenizer = new PdfTokenizer(_reader.RawData);
            int prevXrefPos = tokenizer.FindBackward(startxrefPattern, -1);
            if (prevXrefPos >= 0)
            {
                tokenizer.Position = prevXrefPos + startxrefPattern.Length;
                tokenizer.SkipWhitespaceAndComments();
                var tok = tokenizer.NextToken();
                trailer.Entries["Prev"] = new PdfNumber(long.Parse(tok.Value));
            }

            trailer.WriteTo(ctx);
            ctx.Write($"\nstartxref\n{xrefOffset}\n%%EOF\n");

            return _output.ToArray();
        }
    }
}
