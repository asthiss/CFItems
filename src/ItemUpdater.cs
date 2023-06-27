using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CFItems
{
    public static class ItemUpdater
    {
        private static readonly TableService _tableService =
           new TableService(Environment.GetEnvironmentVariable("AzureWebJobsStorage"), "cfitems");

        [FunctionName(nameof(ItemUpdater))]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items")] HttpRequest req,
            ILogger log)
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var item = JsonSerializer.Deserialize<Item>(requestBody);
            await _tableService.InsertItem(item);
            return new OkObjectResult("data updated");
        }
    }
}
