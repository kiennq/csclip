using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace csclip.test
{
    [TestClass]
    public class ProgramTests
    {
        [TestMethod]
        public void TestCopyAndPaste()
        {
            var program = new Program();

            // Copy
            var input = "Test";
            Console.SetIn(new StringReader(input));
            program.Run(new string[] { "copy" });

            // Paste
            var stdout = new StringWriter();
            Console.SetOut(stdout);
            program.Run(new string[] { "paste" });
            Assert.AreEqual(stdout.ToString(), input);
        }

        [TestMethod]
        public void TestCopyAndPasteMultiFormats()
        {
            var program = new Program();

            // Copy
            var inputText = "Test";
            var inputHtml = "<b>Test</b>";
            var outputHtml = @"Version:1.0
StartHTML:00000097
EndHTML:00000197
StartFragment:00000153
EndFragment:00000164
<!DOCTYPE><HTML><HEAD></HEAD><BODY><!--StartFragment --><b>Test</b><!--EndFragment --></BODY></HTML>";

            Console.SetIn(new StringReader("[{\"cf\":\"text\", \"data\":\"" + inputText + "\"}, {\"cf\":\"html\", \"data\":\"" + inputHtml + "\"}]"));
            program.Run(new string[] { "copy" });

            // Paste
            {
                var stdout = new StringWriter();
                Console.SetOut(stdout);
                program.Run(new string[] { "paste" });
                Assert.AreEqual(stdout.ToString(), inputText);
            }
            {
                var stdout = new StringWriter();
                Console.SetOut(stdout);
                program.Run(new string[] { "paste", "-f", "html" });
                Assert.AreEqual(stdout.ToString(), outputHtml);
            }
        }
    }
}
