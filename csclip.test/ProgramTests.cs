using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace csclip.test
{
    [TestClass]
    public class ProgramTests
    {
        class MockConsole
        {
            public StreamWriter Stdin { get; }
            public StreamReader Stdout { get; }

            public MockConsole()
            {
                var input = new MemoryStream();
                Console.SetIn(new StreamReader(input));
                Stdin = new StreamWriter(input);

                var output = new MemoryStream();
                Console.SetOut(new StreamWriter(output));
                Stdout = new StreamReader(output);
            }
        }

        [TestMethod]
        public void TestCopyAndPaste()
        {
            var program = new Program();
            var console = new MockConsole();

            // Copy
            var input = "Test";
            console.Stdin.Write(input);
            program.Run(new string[] { "copy" });

            // Paste
            program.Run(new string[] { "paste" });
            Assert.AreEqual(console.Stdout.ReadToEnd(), input);
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
