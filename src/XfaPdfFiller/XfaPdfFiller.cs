using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace XfaPdfFiller
{
    /// <summary>
    /// Fills XFA forms in PDF documents by manipulating the embedded XFA XML data.
    /// Drop-in replacement for iTextSharp's PdfReader + PdfStamper + XfaForm.FillXfaForm workflow.
    /// </summary>
    public static class XfaPdfService
    {
        private const string XfaDataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";

        /// <summary>
        /// Fills an XFA form in a PDF template with XML data.
        /// Compatible signature with the original iTextSharp-based GenerarPDF method.
        /// </summary>
        /// <param name="streamXml">MemoryStream containing the XML data to inject into the XFA form.</param>
        /// <param name="pdfTemplate">File path to the PDF template containing the XFA form.</param>
        /// <returns>The filled PDF as a byte array.</returns>
        public static byte[] GenerarPDF(MemoryStream streamXml, string pdfTemplate)
        {
            byte[] pdfData = File.ReadAllBytes(pdfTemplate);
            return FillXfaForm(pdfData, streamXml);
        }

        /// <summary>
        /// Fills an XFA form in a PDF with XML data.
        /// </summary>
        /// <param name="pdfData">The PDF file contents as a byte array.</param>
        /// <param name="xmlStream">Stream containing the XML data to inject.</param>
        /// <returns>The filled PDF as a byte array.</returns>
        public static byte[] FillXfaForm(byte[] pdfData, Stream xmlStream)
        {
            // 1. Parse the PDF and locate the XFA structure
            var reader = new PdfDocumentReader(pdfData);
            var (xfaObj, acroFormDict, acroFormObjNum, catalogDict, catalogObjNum) = reader.GetXfaObject();

            if (xfaObj == null)
                throw new InvalidOperationException("No XFA data found in this PDF.");

            // 2. Load the input XML data
            var inputXml = new XmlDocument();
            inputXml.PreserveWhitespace = true;
            xmlStream.Position = 0;
            inputXml.Load(xmlStream);
            XmlNode inputNode = inputXml.DocumentElement!;

            // 3. Read existing XFA XML and determine structure
            XfaStructure xfa = ReadXfaStructure(reader, xfaObj);

            // 4. Modify the XFA datasets XML with new data
            XmlDocument xfaDoc = xfa.FullDocument;
            XmlNode datasetsNode = FindOrCreateDatasetsNode(xfaDoc);
            InjectDataIntoDatasets(xfaDoc, datasetsNode, inputNode);

            // 5. Remove /Perms from Catalog if present (usage rights signature
            //    becomes invalid after modification, same as iTextSharp does)
            bool catalogModified = false;
            if (catalogDict != null && catalogDict.Entries.ContainsKey("Perms"))
            {
                catalogDict.Entries.Remove("Perms");
                catalogModified = true;
            }

            // 6. Write the modified PDF with incremental update
            return WriteModifiedPdf(reader, xfa, xfaDoc, acroFormDict!, acroFormObjNum,
                                    catalogModified ? catalogDict : null, catalogObjNum);
        }

        /// <summary>
        /// Fills an XFA form in a PDF with XML data provided as a byte array.
        /// </summary>
        public static byte[] FillXfaForm(byte[] pdfData, byte[] xmlData)
        {
            using (var ms = new MemoryStream(xmlData))
                return FillXfaForm(pdfData, ms);
        }

        /// <summary>
        /// Fills an XFA form in a PDF with XML data provided as a string.
        /// </summary>
        public static byte[] FillXfaForm(byte[] pdfData, string xmlString)
        {
            byte[] xmlBytes = Encoding.UTF8.GetBytes(xmlString);
            using (var ms = new MemoryStream(xmlBytes))
                return FillXfaForm(pdfData, ms);
        }

        private class XfaStructure
        {
            public XmlDocument FullDocument { get; set; } = new XmlDocument();
            public bool IsArray { get; set; }
            public PdfArray? OriginalArray { get; set; }

            // For array-based XFA: maps packet names to their object references
            public Dictionary<string, int> PacketObjectNumbers { get; } = new Dictionary<string, int>();

            // Individual packet XML documents (for array-based XFA)
            public Dictionary<string, XmlDocument> PacketDocuments { get; } = new Dictionary<string, XmlDocument>();
        }

        private static XfaStructure ReadXfaStructure(PdfDocumentReader reader, PdfObject xfaObj)
        {
            var xfa = new XfaStructure();

            // Resolve reference if needed
            xfaObj = reader.ResolveReference(xfaObj);

            if (xfaObj is PdfArray array)
            {
                xfa.IsArray = true;
                xfa.OriginalArray = array;

                // XFA array: [name1, streamRef1, name2, streamRef2, ...]
                // Each stream is a separate XML packet (template, datasets, config, etc.)
                // We read each one individually - they are NOT concatenated into one XML doc.
                for (int i = 0; i < array.Items.Count; i += 2)
                {
                    string packetName = "";
                    var nameObj = array.Items[i];
                    if (nameObj is PdfString pdfStr)
                        packetName = pdfStr.Value;
                    else if (nameObj is PdfName pdfName)
                        packetName = pdfName.Name;

                    if (i + 1 < array.Items.Count)
                    {
                        var streamRef = array.Items[i + 1];
                        int objNum = -1;
                        if (streamRef is PdfReference r)
                        {
                            objNum = r.ObjectNumber;
                            streamRef = reader.ReadObject(r.ObjectNumber);
                        }

                        if (streamRef is PdfStream stream)
                        {
                            byte[] data = reader.GetDecompressedStreamBytes(stream);

                            if (objNum >= 0)
                                xfa.PacketObjectNumbers[packetName] = objNum;

                            try
                            {
                                var packetDoc = new XmlDocument();
                                packetDoc.PreserveWhitespace = true;
                                packetDoc.LoadXml(Encoding.UTF8.GetString(data));
                                xfa.PacketDocuments[packetName] = packetDoc;
                            }
                            catch
                            {
                                // Not all packets may be valid standalone XML (preamble/postamble)
                            }
                        }
                    }
                }

                // For array-based XFA, use the datasets packet as our working document
                if (xfa.PacketDocuments.TryGetValue("datasets", out var datasetsDoc))
                {
                    xfa.FullDocument = datasetsDoc;
                }
                else
                {
                    // Create a minimal datasets document
                    xfa.FullDocument.LoadXml(
                        "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"/>");
                }
            }
            else if (xfaObj is PdfStream singleStream)
            {
                xfa.IsArray = false;
                byte[] data = reader.GetDecompressedStreamBytes(singleStream);
                xfa.FullDocument.PreserveWhitespace = true;
                xfa.FullDocument.LoadXml(Encoding.UTF8.GetString(data));
            }
            else
            {
                throw new InvalidOperationException("XFA object is neither an array nor a stream");
            }

            return xfa;
        }

        private static XmlNode FindOrCreateDatasetsNode(XmlDocument doc)
        {
            // Search for the datasets node in the XFA document
            // It can be at different levels depending on the XFA structure
            var nodes = doc.GetElementsByTagName("datasets", XfaDataNamespace);
            if (nodes.Count > 0)
                return nodes[0]!;

            // Also try without namespace
            nodes = doc.GetElementsByTagName("xfa:datasets");
            if (nodes.Count > 0)
                return nodes[0]!;

            // Try local name search through all elements
            XmlNode? datasets = FindNodeByLocalName(doc.DocumentElement!, "datasets");
            if (datasets != null)
                return datasets;

            // Create a new datasets node
            XmlNode? root = doc.DocumentElement;
            if (root == null)
                throw new InvalidOperationException("XFA document has no root element");

            var datasetsElement = doc.CreateElement("xfa", "datasets", XfaDataNamespace);
            datasetsElement.SetAttribute("xmlns:xfa", XfaDataNamespace);
            root.AppendChild(datasetsElement);
            return datasetsElement;
        }

        private static XmlNode? FindNodeByLocalName(XmlNode parent, string localName)
        {
            if (parent.LocalName == localName)
                return parent;

            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                {
                    if (child.LocalName == localName)
                        return child;
                    var found = FindNodeByLocalName(child, localName);
                    if (found != null)
                        return found;
                }
            }
            return null;
        }

        private static void InjectDataIntoDatasets(XmlDocument doc, XmlNode datasetsNode, XmlNode inputNode)
        {
            // Replicate the exact behavior of iTextSharp's FillXfaForm:
            // 1. Find the xfa:data element within datasets
            // 2. If it exists, replace its first element child with the input
            // 3. If it doesn't exist, create it and append the input

            XmlNode? dataNode = null;
            foreach (XmlNode child in datasetsNode.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element &&
                    child.LocalName == "data" &&
                    child.NamespaceURI == XfaDataNamespace)
                {
                    dataNode = child;
                    break;
                }
            }

            if (dataNode == null)
            {
                dataNode = doc.CreateElement("xfa", "data", XfaDataNamespace);
                datasetsNode.AppendChild(dataNode);
            }

            // Import the input node into this document
            XmlNode importedNode = doc.ImportNode(inputNode, true);

            // Find first element child of data node
            XmlNode? firstElement = null;
            foreach (XmlNode child in dataNode.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                {
                    firstElement = child;
                    break;
                }
            }

            if (firstElement != null)
            {
                dataNode.ReplaceChild(importedNode, firstElement);
            }
            else
            {
                dataNode.AppendChild(importedNode);
            }
        }

        private static byte[] WriteModifiedPdf(
            PdfDocumentReader reader,
            XfaStructure xfa,
            XmlDocument modifiedDoc,
            PdfDictionary acroFormDict,
            int acroFormObjNum,
            PdfDictionary? modifiedCatalog,
            int catalogObjNum)
        {
            using (var output = new MemoryStream())
            {
                var writer = new PdfIncrementalWriter(reader, output);

                if (xfa.IsArray && xfa.OriginalArray != null)
                {
                    // For array-based XFA, replace the "datasets" stream
                    var newArray = new PdfArray();
                    for (int i = 0; i < xfa.OriginalArray.Items.Count; i += 2)
                    {
                        string packetName = "";
                        var nameObj = xfa.OriginalArray.Items[i];
                        if (nameObj is PdfString pdfStr)
                            packetName = pdfStr.Value;
                        else if (nameObj is PdfName pdfName)
                            packetName = pdfName.Name;

                        newArray.Items.Add(xfa.OriginalArray.Items[i]);

                        if (packetName == "datasets")
                        {
                            XmlNode? datasetsNode = FindNodeByLocalName(modifiedDoc.DocumentElement!, "datasets");
                            byte[] xmlBytes = SerializeXmlNode(datasetsNode ?? modifiedDoc.DocumentElement!);
                            byte[] compressed = PdfDocumentReader.FlateCompress(xmlBytes);

                            var streamDict = new PdfDictionary();
                            streamDict.Entries["Length"] = new PdfNumber(compressed.Length);
                            streamDict.Entries["Filter"] = new PdfName("FlateDecode");
                            var newStream = new PdfStream(streamDict, compressed);

                            var newRef = writer.WriteNewObject(newStream);
                            newArray.Items.Add(newRef);
                        }
                        else if (i + 1 < xfa.OriginalArray.Items.Count)
                        {
                            newArray.Items.Add(xfa.OriginalArray.Items[i + 1]);
                        }
                    }

                    acroFormDict.Entries["XFA"] = newArray;

                    if (acroFormObjNum >= 0)
                    {
                        var entry = reader.Xref.ContainsKey(acroFormObjNum) ? reader.Xref[acroFormObjNum] : default;
                        writer.WriteReplacementObject(acroFormObjNum, entry.Generation, acroFormDict);
                    }
                    else
                    {
                        writer.WriteNewObject(acroFormDict);
                    }
                }
                else
                {
                    byte[] xmlBytes = SerializeXmlNode(modifiedDoc);
                    byte[] compressed = PdfDocumentReader.FlateCompress(xmlBytes);

                    var streamDict = new PdfDictionary();
                    streamDict.Entries["Length"] = new PdfNumber(compressed.Length);
                    streamDict.Entries["Filter"] = new PdfName("FlateDecode");
                    var newStream = new PdfStream(streamDict, compressed);

                    var newRef = writer.WriteNewObject(newStream);
                    acroFormDict.Entries["XFA"] = newRef;

                    if (acroFormObjNum >= 0)
                    {
                        var entry = reader.Xref.ContainsKey(acroFormObjNum) ? reader.Xref[acroFormObjNum] : default;
                        writer.WriteReplacementObject(acroFormObjNum, entry.Generation, acroFormDict);
                    }
                }

                // Write updated Catalog if modified (e.g., /Perms removed)
                if (modifiedCatalog != null && catalogObjNum >= 0)
                {
                    var entry = reader.Xref.ContainsKey(catalogObjNum) ? reader.Xref[catalogObjNum] : default;
                    writer.WriteReplacementObject(catalogObjNum, entry.Generation, modifiedCatalog);
                }

                return writer.Finish();
            }
        }

        private static byte[] SerializeXmlNode(XmlNode node)
        {
            using (var ms = new MemoryStream())
            {
                var settings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false),
                    Indent = false,
                    OmitXmlDeclaration = node.NodeType != XmlNodeType.Document
                };

                using (var xmlWriter = XmlWriter.Create(ms, settings))
                {
                    node.WriteTo(xmlWriter);
                }

                return ms.ToArray();
            }
        }
    }
}
