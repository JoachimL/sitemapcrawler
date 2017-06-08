using Microsoft.Extensions.CommandLineUtils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SitemapCrawler
{
    class Program
    {

        static void Main(string[] args)
        {
            MainAsync(args).Wait();
        }

        private static async Task MainAsync(string[] args)
        {
            CommandLineApplication commandLineApplication =
                            new CommandLineApplication(throwOnUnexpectedArg: false);
            CommandOption commaSeparatedSitemapUrls = commandLineApplication.Option(
               "-sms |--sitemaps <sitemaps>",
               "Comma-separated list of sitemap URLs.",
               CommandOptionType.SingleValue);
            CommandOption sitemapUrls = commandLineApplication.Option(
               "-sm |--sitemap <sitemap>",
               "A sitemap URL to crawl.",
               CommandOptionType.MultipleValue);
            CommandOption processors = commandLineApplication.Option(
               "-p |--processors <processors>",
               "The maximum number of processors to use.",
               CommandOptionType.SingleValue);
            commandLineApplication.HelpOption("-? | -h | --help");

            IEnumerable<Task> tasks = null; ;

            commandLineApplication.OnExecute(() =>
            {
                var sitemaps = sitemapUrls.Values.Concat(commaSeparatedSitemapUrls.Value()?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ?? Enumerable.Empty<string>());
                if (!sitemaps.Any())
                {
                    Console.WriteLine("No sitemap URLs provided!");
                    commandLineApplication.ShowHelp();
                    return 1;
                }

                Console.WriteLine($"Crawling sitemaps: {string.Join(", ", sitemaps)}");
                tasks = sitemaps.Select(sm => CrawlSitemap(sm, GetNumberOfProcessors(processors))).ToArray();
                return 0;
            });
            int result = commandLineApplication.Execute(args);
            if (result == 0 && tasks != null)
            {
                Console.WriteLine("Waiting for crawlers to finish...");
                await Task.WhenAll(tasks.ToArray());
                Console.WriteLine("Crawling done.");
            }
        }

        private static int GetNumberOfProcessors(CommandOption processors)
        {
            int numberOfProcessors = 0;
            if ((!string.IsNullOrWhiteSpace(processors.Value()) && int.TryParse(processors.Value(), out numberOfProcessors)))
            {
                return numberOfProcessors;
            }
            return 10;
        }

        private static async Task CrawlSitemap(string sitemapUrl, int maxNumberOfProcessors = 10)
        {
            Console.WriteLine("Getting sitemap from " + sitemapUrl + "...");
            var response = await new HttpClient().GetAsync(sitemapUrl);
            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync();
            StringReader reader = new StringReader(xml);
            XDocument doc = XDocument.Load(reader);
            var client = new HttpClient();
            Processor p = new Processor(maxNumberOfProcessors);
            foreach (var link in doc.Descendants("{" + doc.Root.GetDefaultNamespace() + "}loc"))
            {
                await p.QueueItemAsync((string)link.Value);
            }
            await p.WaitForCompleteAsync();
        }
    }
    public class Processor
    {
        public Processor(int maxProcessors)
        {
            _semaphore = new SemaphoreSlim(maxProcessors);
        }

        SemaphoreSlim _semaphore;
        HashSet<Task> _pending = new HashSet<Task>();
        object _lock = new Object();
        private HttpClient httpClient = new HttpClient();

        async Task ProcessAsync(string data)
        {
            await _semaphore.WaitAsync();
            try
            {
                await Task.Run(async () =>
                {
                    var result = await httpClient.GetAsync(data);
                    Console.WriteLine(string.Concat(result.StatusCode, "\t", data));
                    if (!result.IsSuccessStatusCode)
                        throw new ArgumentException($"Error occured when attempting to read {data}.");
                });
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task QueueItemAsync(string url)
        {
            var task = ProcessAsync(url);
            lock (_lock)
                _pending.Add(task);
            try
            {
                await task;
            }
            catch
            {
                if (!task.IsCanceled && !task.IsFaulted)
                    throw; // not the task's exception, rethrow
                           // don't remove faulted/cancelled tasks from the list
                return;
            }
            // remove successfully completed tasks from the list 
            lock (_lock)
                _pending.Remove(task);
        }

        public async Task WaitForCompleteAsync()
        {
            Task[] tasks;
            lock (_lock)
                tasks = _pending.ToArray();
            await Task.WhenAll(tasks);
        }
    }
}