using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Text;
using Newtonsoft.Json;
using Windows.ApplicationModel.DataTransfer;
using Application = System.Windows.Forms.Application;

namespace csclip
{
    public class Program
    {

        [Verb("copy", HelpText = "Copy to clipboard through pipe using data format {\"cf\":, \"data\":}")]
        class CopyOptions { }

        [Verb("paste", HelpText = "Get content from clipboard")]
        class PasteOptions
        {
            [Option('f', "format", Default = "text", HelpText = "Clipboard format. Supported format: <text|html>")]
            public string Format { get; set; }
        }

        [Verb("server", HelpText = "Interactively get/put data to clipboard. Data format <size>\\r\\n{\"command\":, \"data\":}")]
        class ServerOptions { }

        static string ConvertToClipboardFormat(string format)
        {
            switch(format)
            {
                case "text":
                    return StandardDataFormats.Text;
                case "html":
                    return StandardDataFormats.Html;
                default:
                    return format;
            }
        }

        struct ClipboardData
        {
            public string cf; // Clipboard format
            public string data;
        }

        static ClipboardData NormalizeClipboardData(ClipboardData org)
        {
            var norm = new ClipboardData();
            switch(org.cf)
            {
                case "text":
                    norm.cf = StandardDataFormats.Text;
                    norm.data = org.data;
                    break;
                case "html":
                    norm.cf = StandardDataFormats.Html;
                    norm.data = HtmlFormatHelper.CreateHtmlFormat(org.data);
                    break;
                default:
                    return org;
            }

            return norm;
        }

        async Task<int> DoCopy(CopyOptions opts)
        {
            var package = new DataPackage();
            var text = await Console.In.ReadToEndAsync();
            try
            {
                switch (text[0])
                {
                    case '[':
                        {
                            var datas = JsonConvert.DeserializeObject<ClipboardData[]>(text);
                            foreach (var data in datas)
                            {
                                var norm = NormalizeClipboardData(data);
                                package.SetData(norm.cf, norm.data);
                            }

                            break;
                        }
                    case '{':
                        {
                            var data = JsonConvert.DeserializeObject<ClipboardData>(text);
                            var norm = NormalizeClipboardData(data);
                            package.SetData(norm.cf, norm.data);
                            break;
                        }
                    default:
                        package.SetText(text);
                        break;
                }
            }
            catch (JsonException)
            {
                package.SetText(text);
            }

            Clipboard.SetContent(package);
            Clipboard.Flush();
            return 0;
        }

        async Task<int> DoPaste(PasteOptions opts)
        {
            var data = Clipboard.GetContent();
            var format = ConvertToClipboardFormat(opts.Format);
            if (data.Contains(format))
            {
                Console.Write(await data.GetDataAsync(format));
            }

            return 0;
            }

        int DoRunServer(ServerOptions opts)
        {
            return 0;
        }

        public int Run(string[] args)
        {
            return Parser.Default.ParseArguments<CopyOptions, PasteOptions, ServerOptions>(args)
                   .MapResult(
                        (CopyOptions opts) => DoCopy(opts).Result,
                        (PasteOptions opts) => DoPaste(opts).Result,
                        (ServerOptions opts) => DoRunServer(opts),
                        errs => 0);
        }

        static void Main(string[] args)
        {
            var program = new Program();

            var inputThread = new Thread(() =>
            {
                try
                {
                    program.Run(args);
                }
                catch(Exception e)
                {
                    Console.Error.WriteLine(e.Message);
                }

                Application.Exit();
            });
            inputThread.SetApartmentState(ApartmentState.STA);
            inputThread.Start();

            // Message pump
            Application.Run();
        }

    }
}
