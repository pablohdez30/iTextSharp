using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Xunit;

namespace XfaPdfFiller.Tests
{
    public class XfaPdfFillerTests
    {
        #region Array-based XFA Tests

        [Fact]
        public void FillXfaForm_ArrayBased_ReturnsValidPdf()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();
            string xml = @"<form1><nombre>Juan</nombre><apellido>García</apellido><email>juan@test.com</email></form1>";

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, xml);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Length > pdf.Length, "Filled PDF should be larger due to incremental update");
            AssertValidPdf(result);
        }

        [Fact]
        public void FillXfaForm_ArrayBased_ContainsInjectedData()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();
            string xml = @"<form1><nombre>María</nombre><apellido>López</apellido><email>maria@ejemplo.com</email></form1>";

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, xml);

            // Assert - Read back the XFA data from the result PDF
            string xfaContent = ExtractDatasetsXml(result);
            Assert.Contains("María", xfaContent);
            Assert.Contains("López", xfaContent);
            Assert.Contains("maria@ejemplo.com", xfaContent);
        }

        [Fact]
        public void FillXfaForm_ArrayBased_ReplacesExistingData()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();
            string xml1 = @"<form1><nombre>Primero</nombre></form1>";
            string xml2 = @"<form1><nombre>Segundo</nombre></form1>";

            // Act - Fill twice
            byte[] result1 = XfaPdfService.FillXfaForm(pdf, xml1);
            byte[] result2 = XfaPdfService.FillXfaForm(result1, xml2);

            // Assert
            string xfaContent = ExtractDatasetsXml(result2);
            Assert.Contains("Segundo", xfaContent);
        }

        #endregion

        #region Single-stream XFA Tests

        [Fact]
        public void FillXfaForm_SingleStream_ReturnsValidPdf()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateSingleStreamXfaPdf();
            string xml = @"<form1><nombre>Pedro</nombre><apellido>Martínez</apellido></form1>";

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, xml);

            // Assert
            Assert.NotNull(result);
            AssertValidPdf(result);
        }

        [Fact]
        public void FillXfaForm_SingleStream_ContainsInjectedData()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateSingleStreamXfaPdf();
            string xml = @"<form1><nombre>Ana</nombre><email>ana@test.com</email></form1>";

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, xml);

            // Assert
            string xfaContent = ExtractAllXfaXml(result);
            Assert.Contains("Ana", xfaContent);
            Assert.Contains("ana@test.com", xfaContent);
        }

        #endregion

        #region GenerarPDF Compatibility Tests

        [Fact]
        public void GenerarPDF_WithFilePath_ReturnsFilledPdf()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();
            string tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tempPath, pdf);

                string xmlStr = @"<form1><nombre>Carlos</nombre><apellido>Ruiz</apellido></form1>";
                using var xmlStream = new MemoryStream(Encoding.UTF8.GetBytes(xmlStr));

                // Act
                byte[] result = XfaPdfService.GenerarPDF(xmlStream, tempPath);

                // Assert
                Assert.NotNull(result);
                AssertValidPdf(result);
                string content = ExtractDatasetsXml(result);
                Assert.Contains("Carlos", content);
                Assert.Contains("Ruiz", content);
            }
            finally
            {
                File.Delete(tempPath);
            }
        }

        #endregion

        #region Stream Overload Tests

        [Fact]
        public void FillXfaForm_WithStream_Works()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();
            string xml = @"<form1><nombre>Test</nombre></form1>";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, stream);

            // Assert
            AssertValidPdf(result);
        }

        [Fact]
        public void FillXfaForm_WithByteArray_Works()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();
            byte[] xml = Encoding.UTF8.GetBytes(@"<form1><nombre>Test</nombre></form1>");

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, xml);

            // Assert
            AssertValidPdf(result);
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void FillXfaForm_WithSpecialCharacters_PreservesEncoding()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();
            string xml = @"<form1><nombre>José María Ñoño</nombre><apellido>Ü'ñáéíóú</apellido></form1>";

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, xml);

            // Assert
            string content = ExtractDatasetsXml(result);
            Assert.Contains("José María Ñoño", content);
            Assert.Contains("Ü'ñáéíóú", content);
        }

        [Fact]
        public void FillXfaForm_WithEmptyFields_Works()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();
            string xml = @"<form1><nombre></nombre><apellido/><email></email></form1>";

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, xml);

            // Assert
            AssertValidPdf(result);
        }

        [Fact]
        public void FillXfaForm_WithExtraFields_Works()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();
            string xml = @"<form1><nombre>Test</nombre><campoExtra>Valor</campoExtra><otroCampo>123</otroCampo></form1>";

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, xml);

            // Assert
            AssertValidPdf(result);
            string content = ExtractDatasetsXml(result);
            Assert.Contains("campoExtra", content);
        }

        [Fact]
        public void FillXfaForm_WithNestedXml_Works()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();
            string xml = @"<form1><direccion><calle>Principal 123</calle><ciudad>Madrid</ciudad></direccion><nombre>Test</nombre></form1>";

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, xml);

            // Assert
            AssertValidPdf(result);
            string content = ExtractDatasetsXml(result);
            Assert.Contains("Principal 123", content);
            Assert.Contains("Madrid", content);
        }

        [Fact]
        public void FillXfaForm_NoPdf_ThrowsException()
        {
            // Arrange
            byte[] invalidPdf = Encoding.UTF8.GetBytes("This is not a PDF");
            string xml = @"<form1><nombre>Test</nombre></form1>";

            // Act & Assert
            Assert.ThrowsAny<Exception>(() => XfaPdfService.FillXfaForm(invalidPdf, xml));
        }

        [Fact]
        public void FillXfaForm_ResultCanBeFilledAgain()
        {
            // Arrange - Simulate multiple fill operations like a real workflow
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf();

            // Act - Fill, then fill the result again
            byte[] filled1 = XfaPdfService.FillXfaForm(pdf, @"<form1><nombre>Paso1</nombre></form1>");
            byte[] filled2 = XfaPdfService.FillXfaForm(filled1, @"<form1><nombre>Paso2</nombre></form1>");

            // Assert
            AssertValidPdf(filled2);
            string content = ExtractDatasetsXml(filled2);
            Assert.Contains("Paso2", content);
        }

        #endregion

        #region Custom Fields Tests

        [Fact]
        public void FillXfaForm_CustomFieldNames_Works()
        {
            // Arrange
            byte[] pdf = XfaPdfTestHelper.CreateMinimalXfaPdf(
                rootDataElement: "solicitud",
                fieldNames: new[] { "fechaSolicitud", "monto", "plazo", "referencia" });

            string xml = @"<solicitud><fechaSolicitud>2025-01-15</fechaSolicitud><monto>50000.00</monto><plazo>12</plazo><referencia>REF-001</referencia></solicitud>";

            // Act
            byte[] result = XfaPdfService.FillXfaForm(pdf, xml);

            // Assert
            AssertValidPdf(result);
            string content = ExtractDatasetsXml(result);
            Assert.Contains("2025-01-15", content);
            Assert.Contains("50000.00", content);
            Assert.Contains("REF-001", content);
        }

        #endregion

        #region Helpers

        private static void AssertValidPdf(byte[] data)
        {
            Assert.True(data.Length > 0, "PDF data should not be empty");

            // Check PDF header
            string header = Encoding.ASCII.GetString(data, 0, Math.Min(8, data.Length));
            Assert.StartsWith("%PDF", header);

            // Check for %%EOF
            string tail = Encoding.ASCII.GetString(data, Math.Max(0, data.Length - 50), Math.Min(50, data.Length));
            Assert.Contains("%%EOF", tail);
        }

        private static string ExtractDatasetsXml(byte[] pdfData)
        {
            // Use our own reader to extract the XFA datasets
            var reader = new PdfDocumentReader(pdfData);
            var (xfaObj, _, _) = reader.GetXfaObject();

            xfaObj = reader.ResolveReference(xfaObj!);

            if (xfaObj is PdfArray array)
            {
                for (int i = 0; i < array.Items.Count; i += 2)
                {
                    string name = "";
                    var nameObj = array.Items[i];
                    if (nameObj is PdfString ps) name = ps.Value;
                    else if (nameObj is PdfName pn) name = pn.Name;

                    if (name == "datasets" && i + 1 < array.Items.Count)
                    {
                        var streamRef = array.Items[i + 1];
                        var resolved = reader.ResolveReference(streamRef);
                        if (resolved is PdfStream stream)
                        {
                            byte[] decompressed = reader.GetDecompressedStreamBytes(stream);
                            return Encoding.UTF8.GetString(decompressed);
                        }
                    }
                }
            }

            return ExtractAllXfaXml(pdfData);
        }

        private static string ExtractAllXfaXml(byte[] pdfData)
        {
            var reader = new PdfDocumentReader(pdfData);
            var (xfaObj, _, _) = reader.GetXfaObject();

            xfaObj = reader.ResolveReference(xfaObj!);

            if (xfaObj is PdfStream singleStream)
            {
                byte[] decompressed = reader.GetDecompressedStreamBytes(singleStream);
                return Encoding.UTF8.GetString(decompressed);
            }

            if (xfaObj is PdfArray array)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < array.Items.Count; i += 2)
                {
                    if (i + 1 < array.Items.Count)
                    {
                        var resolved = reader.ResolveReference(array.Items[i + 1]);
                        if (resolved is PdfStream stream)
                        {
                            byte[] data = reader.GetDecompressedStreamBytes(stream);
                            sb.Append(Encoding.UTF8.GetString(data));
                        }
                    }
                }
                return sb.ToString();
            }

            return "";
        }

        #endregion
    }
}
