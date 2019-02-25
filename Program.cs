using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace csclip
{
    class Program
    {
        public static void Main(string[] args)
        {
            Task.Run(() =>
            {
                // Message pump
                Application.Run();
            });

            var wg = new Barrier(1);
            var program = Program.CreateInstance();

            if (args.Length > 0)
            {
                Console.WriteLine(program.ProcessRequestAsync(args).Result);
            }
            else
            {
                string s;
                while ((s = Console.ReadLine()) != null)
                {
                    Console.WriteLine(program.ProcessRequestAsync(s.Split()).Result);
                }
            }

            Application.Exit();
        }

        static Program CreateInstance()
        {
            return new Program();
        }

        async Task<string> ProcessRequestAsync(string[] args)
        {
            return await Task.FromResult<string>(string.Join("+", args));
        }
    }
}
