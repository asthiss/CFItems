using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace CFItems
{
    public static class ItemShower
    {
        private static readonly TableService _tableService = 
            new TableService(Environment.GetEnvironmentVariable("AzureWebJobsStorage"), "cfitems");
       
        [FunctionName(nameof(ItemShower))]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/{call}")] HttpRequest req,
            ILogger log)
        {
            try
            {
                var items = _tableService.GetAllItemsAsync();
                return new OkObjectResult(JsonSerializer.Serialize(items));
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult(ex);
            }
        }
    }
}
