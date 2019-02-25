using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CommandLine;

namespace csclip
{
    class Program
    {
        [Verb("copy", HelpText = "Copy to clipboard through pipe using data format {\"cf\":, \"data\":}")]
        class CopyOptions { }

        [Verb("paste", HelpText = "Get content from clipboard")]
        class PasteOptions { }

        [Verb("server", HelpText = "Interactively get/put data to clipboard. Data format <size>\\r\\n{\"command\":, \"data\":}")]
        class ServerOptions { }

        static Program CreateInstance()
        {
            return new Program();
        }

        int DoCopy(CopyOptions opts)
        {
            return 0;
        }

        int DoPaste(PasteOptions opts)
        {
            return 0;
        }

        int DoRunServer(ServerOptions opts)
        {
            return 0;
        }

        public static void Main(string[] args)
        {
            var program = Program.CreateInstance();

            Task.Run(() =>
            {
                Parser.Default.ParseArguments<CopyOptions, PasteOptions, ServerOptions>(args)
                .MapResult(
                    (CopyOptions opts) => program.DoCopy(opts),
                    (PasteOptions opts) => program.DoPaste(opts),
                    (ServerOptions opts) => program.DoRunServer(opts),
                    errs => 0);

                Application.Exit();
            });

            // Message pump
            Application.Run();
        }

    }
}
