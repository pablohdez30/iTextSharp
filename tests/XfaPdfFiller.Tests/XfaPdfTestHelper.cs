using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XfaPdfFiller.Tests
{
    /// <summary>
    /// Helper to create minimal XFA PDF files for testing purposes.
    /// These PDFs contain a valid XFA form structure that can be filled.
    /// </summary>
    internal static class XfaPdfTestHelper
    {
        /// <summary>
        /// Creates a minimal PDF with an XFA form (array-based XFA structure).
        /// The form has a datasets section with a data node containing fields.
        /// </summary>
        public static byte[] CreateMinimalXfaPdf(string rootDataElement = "form1", string[] fieldNames = null!)
        {
            if (fieldNames == null)
                fieldNames = new[] { "nombre", "apellido", "email" };

            // Build XFA XML packets
            string templateXml = BuildTemplateXml(rootDataElement, fieldNames);
            string datasetsXml = BuildDatasetsXml(rootDataElement, fieldNames);
            string configXml = BuildConfigXml();

            byte[] templateBytes = Encoding.UTF8.GetBytes(templateXml);
            byte[] datasetsBytes = Encoding.UTF8.GetBytes(datasetsXml);
            byte[] configBytes = Encoding.UTF8.GetBytes(configXml);

            // Compress streams
            byte[] templateCompressed = FlateCompress(templateBytes);
            byte[] datasetsCompressed = FlateCompress(datasetsBytes);
            byte[] configCompressed = FlateCompress(configBytes);

            return BuildPdfWithXfa(templateCompressed, templateBytes.Length,
                                    datasetsCompressed, datasetsBytes.Length,
                                    configCompressed, configBytes.Length);
        }

        /// <summary>
        /// Creates a minimal PDF with a single-stream XFA (not array-based).
        /// </summary>
        public static byte[] CreateSingleStreamXfaPdf(string rootDataElement = "form1", string[] fieldNames = null!)
        {
            if (fieldNames == null)
                fieldNames = new[] { "nombre", "apellido", "email" };

            string fullXfa = BuildFullXfaXml(rootDataElement, fieldNames);
            byte[] xfaBytes = Encoding.UTF8.GetBytes(fullXfa);
            byte[] xfaCompressed = FlateCompress(xfaBytes);

            return BuildPdfWithSingleStreamXfa(xfaCompressed, xfaBytes.Length);
        }

        private static string BuildTemplateXml(string rootElement, string[] fields)
        {
            var sb = new StringBuilder();
            sb.Append($"<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.0/\">");
            sb.Append($"<subform name=\"{rootElement}\" layout=\"tb\">");
            foreach (var field in fields)
            {
                sb.Append($"<field name=\"{field}\"><ui><textEdit/></ui></field>");
            }
            sb.Append("</subform></template>");
            return sb.ToString();
        }

        private static string BuildDatasetsXml(string rootElement, string[] fields)
        {
            var sb = new StringBuilder();
            sb.Append("<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">");
            sb.Append($"<xfa:data><{rootElement}>");
            foreach (var field in fields)
            {
                sb.Append($"<{field}/>");
            }
            sb.Append($"</{rootElement}></xfa:data></xfa:datasets>");
            return sb.ToString();
        }

        private static string BuildConfigXml()
        {
            return "<config xmlns=\"http://www.xfa.org/schema/xci/3.0/\"><present><pdf><version>1.7</version></pdf></present></config>";
        }

        private static string BuildFullXfaXml(string rootElement, string[] fields)
        {
            var sb = new StringBuilder();
            sb.Append("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">");
            sb.Append(BuildTemplateXml(rootElement, fields));
            sb.Append(BuildDatasetsXml(rootElement, fields));
            sb.Append(BuildConfigXml());
            sb.Append("</xdp:xdp>");
            return sb.ToString();
        }

        private static byte[] FlateCompress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        private static byte[] BuildPdfWithXfa(
            byte[] templateCompressed, int templateOrigLen,
            byte[] datasetsCompressed, int datasetsOrigLen,
            byte[] configCompressed, int configOrigLen)
        {
            using (var ms = new MemoryStream())
            using (var w = new StreamWriter(ms, new UTF8Encoding(false)) { AutoFlush = true })
            {
                // Header
                w.Write("%PDF-1.7\n%\xe2\xe3\xcf\xd3\n");
                long[] objOffsets = new long[10];

                // Object 1: Catalog
                objOffsets[1] = ms.Position;
                w.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>\nendobj\n");

                // Object 2: Pages
                objOffsets[2] = ms.Position;
                w.Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

                // Object 3: Page
                objOffsets[3] = ms.Position;
                w.Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

                // Object 4: Template stream
                objOffsets[4] = ms.Position;
                w.Write($"4 0 obj\n<< /Length {templateCompressed.Length} /Filter /FlateDecode >>\nstream\n");
                w.Flush();
                ms.Write(templateCompressed, 0, templateCompressed.Length);
                w.Write("\nendstream\nendobj\n");

                // Object 5: AcroForm with XFA array
                objOffsets[5] = ms.Position;
                w.Write("5 0 obj\n<< /XFA [(template) 4 0 R (datasets) 6 0 R (config) 7 0 R] >>\nendobj\n");

                // Object 6: Datasets stream
                objOffsets[6] = ms.Position;
                w.Write($"6 0 obj\n<< /Length {datasetsCompressed.Length} /Filter /FlateDecode >>\nstream\n");
                w.Flush();
                ms.Write(datasetsCompressed, 0, datasetsCompressed.Length);
                w.Write("\nendstream\nendobj\n");

                // Object 7: Config stream
                objOffsets[7] = ms.Position;
                w.Write($"7 0 obj\n<< /Length {configCompressed.Length} /Filter /FlateDecode >>\nstream\n");
                w.Flush();
                ms.Write(configCompressed, 0, configCompressed.Length);
                w.Write("\nendstream\nendobj\n");

                // Xref table
                long xrefOffset = ms.Position;
                w.Write("xref\n");
                w.Write("0 8\n");
                w.Write("0000000000 65535 f \n");
                for (int i = 1; i <= 7; i++)
                    w.Write($"{objOffsets[i]:D10} 00000 n \n");

                // Trailer
                w.Write($"trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

                w.Flush();
                return ms.ToArray();
            }
        }

        private static byte[] BuildPdfWithSingleStreamXfa(byte[] xfaCompressed, int xfaOrigLen)
        {
            using (var ms = new MemoryStream())
            using (var w = new StreamWriter(ms, new UTF8Encoding(false)) { AutoFlush = true })
            {
                w.Write("%PDF-1.7\n%\xe2\xe3\xcf\xd3\n");
                long[] objOffsets = new long[10];

                // Object 1: Catalog
                objOffsets[1] = ms.Position;
                w.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>\nendobj\n");

                // Object 2: Pages
                objOffsets[2] = ms.Position;
                w.Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

                // Object 3: Page
                objOffsets[3] = ms.Position;
                w.Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

                // Object 4: AcroForm with XFA single stream
                objOffsets[4] = ms.Position;
                w.Write("4 0 obj\n<< /XFA 5 0 R >>\nendobj\n");

                // Object 5: XFA stream
                objOffsets[5] = ms.Position;
                w.Write($"5 0 obj\n<< /Length {xfaCompressed.Length} /Filter /FlateDecode >>\nstream\n");
                w.Flush();
                ms.Write(xfaCompressed, 0, xfaCompressed.Length);
                w.Write("\nendstream\nendobj\n");

                // Xref
                long xrefOffset = ms.Position;
                w.Write("xref\n");
                w.Write("0 6\n");
                w.Write("0000000000 65535 f \n");
                for (int i = 1; i <= 5; i++)
                    w.Write($"{objOffsets[i]:D10} 00000 n \n");

                w.Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
