using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CFItems
{
    public static class LogEater
    {
        private static readonly string itemDelimiter = "----------------------------------------";
        private static readonly string itemDelimiterLineTwo = "can be referred to as";
        private static readonly TableService _tableService = 
            new TableService(Environment.GetEnvironmentVariable("AzureWebJobsStorage"), "cfitems");
       
        [FunctionName(nameof(LogEater))]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            try
            {
                var lines = new List<string>();
                var reader = new StreamReader(req.Body);
                while (!reader.EndOfStream)
                {
                    lines.Add(reader.ReadLine());
                }
                var readingItem = false;
                var items = new List<Item>();
                Item item = null;
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith(itemDelimiter) &&
                        lines[i + 1].Contains(itemDelimiterLineTwo) &&
                        !lines[i - 1].Contains("lore")) //remove lores
                    {
                        readingItem = true;
                        item = new Item();
                        continue;
                    }

                    if ((readingItem && lines[i].StartsWith(itemDelimiter)) ||
                        (readingItem && string.IsNullOrWhiteSpace(lines[i])))
                    {
                        items.Add(ProcessItem(item, log));
                        readingItem = false;
                        continue;
                    }

                    if (readingItem)
                    {
                        item.Data.Add(lines[i]);
                    }
                }

                foreach(var itemToUpload in items)
                {
                    await _tableService.InsertItem(itemToUpload);
                }

                return new OkObjectResult("Log ingested");
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult(ex);
            }
        }

        private static Item ProcessItem(Item item, ILogger log)
        {
            var description = string.Join(' ', item.Data);
            item.FullDataPiped = string.Join('|', item.Data);
            try
            {
                item.Name = ExtractItemName(item.Data.First());
                item.RowKey = item.Name;
            }
            catch (Exception ex)
            {
                log.LogError(ex, ex.Message + $"Item: {description}");
            }
            
            try
            {
                item.Level = ExtractLevel(description);
            }
            catch (Exception ex)
            {
                log.LogError(ex, ex.Message + $"Item: {description}");
            }
            
            try
            {
                item.Worth = ExtractWorth(description);
            }
            catch (Exception ex)
            {
                log.LogError(ex, ex.Message + $"Item: {description}");
            }
            
            try
            {
                FillGroupAndType(item);
            }
            catch (Exception ex)
            {
                log.LogError(ex, ex.Message + $"Item: {description}");
            }
            
            try
            {
                FillMaterialAndWeight(item);
            }
            catch(Exception ex) {
                log.LogError(ex, ex.Message + $"Item: {description}");
            }

            try
            {
                FillAffects(item);
            }
            catch (Exception ex)
            {
                log.LogError(ex, ex.Message + $"Item: {description}");
            }

            return item;
        }

        private static void FillAffects(Item item)
        {
            var madeOfIndex = item.Data.FindIndex(x => x.StartsWith("It is made of ")) + 1;
            if(item.IsWeapon)
            {
                madeOfIndex = madeOfIndex + 1;
            }

            item.Affects = item.Data.Skip(madeOfIndex).ToList();
            item.AffectsPiped = string.Join('|', item.Affects);
        }

        private static void FillMaterialAndWeight(Item item)
        {
            var input = item.Data.Where(x => x.StartsWith("It is made of ")).First();
            var parts = input.Split(' ');
            item.Material = parts[4];
            item.Weight = $"{parts[7]},{parts[9]}";

            if (input.Contains("pounds"))
            {
                var pounds = double.Parse(parts[7]);
                var kg = Math.Abs(pounds * 0.45359237).ToString();
                item.Weight = kg.Substring(0, kg.IndexOf(",") + 4);
            }

            item.Kg = item.Weight.Split(',').First();
            item.Gram = item.Weight.Split(',').Last();
        }

        private static void FillGroupAndType(Item item)
        {
            var madeOfIndex = item.Data.FindIndex(x => x.StartsWith("It is made of "));
            var groupAndType = item.Data.ElementAt(madeOfIndex - 1) switch
            {
                "It is a talisman" => ("talisman", "talisman"),
                var str when str.StartsWith("It is armor") => ("armor", str.Split(" ").Last().TrimEnd('.')),
                var str when str.StartsWith("It is clothing") => ("clothing", str.Split(" ").Last().TrimEnd('.')),
                var str when str.Contains("with an attack type") => ("weapon", str.Split(" ")[3].TrimEnd('.')),
                var str when str.StartsWith("It is a") || str.StartsWith("It is an") => (str.Split(" ").Last().TrimEnd('.'), str.Split(" ").Last().TrimEnd('.')),
                _ => ("unknow", "unknow")
            };

            item.Group = groupAndType.Item1;
            item.Type = groupAndType.Item2;
            if (item.IsWeapon)
            {
                var damnounStringParts = item.Data.ElementAt(madeOfIndex - 1).Split(" ");
                var damnoun = damnounStringParts.Last().TrimEnd('.');
                if (damnounStringParts[damnounStringParts.Length-2] != "of")
                {
                    damnoun = $"{damnounStringParts[damnounStringParts.Length - 2]} {damnoun}";
                }
                
                item.Damnoun = damnoun;
                var averageString = item.Data.Where(x => x.StartsWith("It can cause ")).First();
                item.Avg = averageString.Split(" ").Last().TrimEnd('.');
            }
        }

        private static string ExtractItemName(string description)
        {
            string[] parts = description.Split(" can be referred to as ", StringSplitOptions.RemoveEmptyEntries);
            return parts[0];
        }

        private static string ExtractLevel(string description)
        {
            string levelPattern = @"(\d+)[a-z][a-z] level of power";
            Match match = Regex.Match(description, levelPattern, RegexOptions.IgnoreCase);
            return match.Groups[1].Value;
        }

        private static int ExtractWorth(string description)
        {
            string worthPattern = @"worth (\d+) copper";
            Match match = Regex.Match(description, worthPattern, RegexOptions.IgnoreCase);
            string worthString = match.Groups[1].Value;
            return int.Parse(worthString);
        }

        private static string ExtractType(string description)
        {
            string[] lines = description.Split('\n');
            string typeLine = Array.Find(lines, line => line.StartsWith("It is "));
            string[] typeParts = typeLine.Split("It is ", StringSplitOptions.RemoveEmptyEntries);
            return typeParts[1].TrimEnd('.');
        }
    }
}
