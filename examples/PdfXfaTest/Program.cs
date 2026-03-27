using System;
using System.IO;
using XfaPdfFiller;

namespace PdfXfa_Test
{
    internal class Program
    {
        static int Main(string[] args)
        {
            try
            {
                string pdfTemplate = (args.Length > 0) ? args[0] : @"C:\Temp\CSF_Form_Anejo1_v1_0.pdf";
                string xmlPath = (args.Length > 1) ? args[1] : @"C:\Temp\CSF_Form_Anejo1_v1_0_datos.xml";
                string outPdf = (args.Length > 2) ? args[2] : @"C:\Temp\OUT_nuevo.pdf";

                if (!File.Exists(pdfTemplate)) throw new FileNotFoundException("No existe el PDF", pdfTemplate);
                if (!File.Exists(xmlPath)) throw new FileNotFoundException("No existe el XML", xmlPath);

                byte[] xmlBytes = File.ReadAllBytes(xmlPath);
                MemoryStream xmlStream = new MemoryStream(xmlBytes);

                byte[] result = XfaPdfService.GenerarPDF(xmlStream, pdfTemplate);

                File.WriteAllBytes(outPdf, result);

                Console.WriteLine("OK generado con XfaPdfFiller: " + outPdf);
                Console.WriteLine("Tamaño: " + result.Length + " bytes");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex);
                return 1;
            }
        }
    }
}
