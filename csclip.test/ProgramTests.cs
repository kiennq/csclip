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
                Stdin = new StreamWriter(input) { AutoFlush = true };

                var output = new MemoryStream();
                Console.SetOut(new StreamWriter(output) {AutoFlush = true });
                Stdout = new StreamReader(output);
            }
        }

        void KeepPositionInvoke(Stream stream, Action action)
        {
            var pos = stream.Position;
            action();
            stream.Position = pos;
        }

        [TestMethod]
        public void TestCopyAndPaste()
        {
            var program = new Program();
            var con = new MockConsole();

            // Copy
            var input = "Test";
            KeepPositionInvoke(con.Stdin.BaseStream, () =>
            {
                con.Stdin.Write(input);
            });
            program.RunAsync(new string[] { "copy" }).Wait();

            // Paste
            KeepPositionInvoke(con.Stdout.BaseStream, () =>
            {
                program.RunAsync(new string[] { "paste" }).Wait();
            });
            Assert.AreEqual(con.Stdout.ReadToEnd(), input);

            // Paste
            KeepPositionInvoke(con.Stdout.BaseStream, () =>
            {
                program.RunAsync(new string[] { "paste", "--rpc-format" }).Wait();
            });
            Assert.AreEqual(con.Stdout.ReadToEnd(), String.Format("33\r\n{{\"command\":\"paste\",\"args\":\"{0}\"}}", input));
        }

        [TestMethod]
        public void TestCopyAndPasteMultiFormats()
        {
            var program = new Program();
            var con = new MockConsole();

            // Copy
            var inputText = "Test";
            var inputHtml = "<b>Test</b>";
            var outputHtml = @"Version:1.0
StartHTML:00000097
EndHTML:00000197
StartFragment:00000153
EndFragment:00000164
<!DOCTYPE><HTML><HEAD></HEAD><BODY><!--StartFragment --><b>Test</b><!--EndFragment --></BODY></HTML>";

            KeepPositionInvoke(con.Stdin.BaseStream, () =>
            {
                con.Stdin.Write(String.Format("[{{\"cf\":\"text\", \"data\":\"{0}\"}}, {{\"cf\":\"html\", \"data\":\"{1}\"}}]", inputText, inputHtml));
            });
            program.RunAsync(new string[] { "copy" }).Wait();

            // Paste
            {
                KeepPositionInvoke(con.Stdout.BaseStream, () =>
                {
                    program.RunAsync(new string[] { "paste" }).Wait();
                });
                Assert.AreEqual(con.Stdout.ReadToEnd(), inputText);
            }
            {
                KeepPositionInvoke(con.Stdout.BaseStream, () =>
                {
                    program.RunAsync(new string[] { "paste", "-f", "html" }).Wait();
                });
                Assert.AreEqual(con.Stdout.ReadToEnd(), outputHtml);
            }
        }

        [TestMethod]
        public void TestServer()
        {
            var program = new Program();
            var con = new MockConsole();
        }
    }
}
