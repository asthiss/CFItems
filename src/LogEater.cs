using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;

namespace CFItems
{
    public static class LogEater
    {
        private static readonly Regex wandRegex = new Regex("contains the spell '(.*)' of the (\\d*).. level");
        private static readonly Regex spellsRegex = new Regex("'(\\w* ?\\w*?)'");
        private static readonly Regex modifierRegex = new Regex("your \\b(\\w*\\b ?\\w*\\b ?\\w*)\\b ?\\b\\w*\\b by (-?\\d*%?)");
        private static readonly Regex numRegex = new Regex("(\\d+)");
        private static readonly string itemDelimiter = "----------------------------------------";
        private static readonly string itemDelimiterLineTwo = "can be referred to as";
        private static string fileName = string.Empty;
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
                if (req.Headers.TryGetValue("filename", out var filename))
                {
                    fileName = filename;
                }
                await UploadFileToBlobStorage(filename, "orginal", req.Body);
                req.Body.Position = 0;
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
                        items.Add(await ProcessItem(item, log));
                        readingItem = false;
                        continue;
                    }

                    if (readingItem)
                    {
                        item.Data.Add(lines[i]);
                    }
                }

                var justItemLines = new List<string>();
                foreach (var itemToUpload in items)
                {
                    await _tableService.InsertItem(itemToUpload);
                    justItemLines.AddRange(itemToUpload.Data);
                    justItemLines.Add("------");
                }

                var writer = new StreamWriter(new MemoryStream())
                {
                    AutoFlush = true
                };
                foreach (var line in justItemLines)
                {
                    writer.WriteLine(line);
                }
                writer.Flush();
                await UploadFileToBlobStorage("parsed" + filename, "parsed", writer.BaseStream);

                return new OkObjectResult("Log ingested");
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult(ex);
            }
        }

        private static async Task UploadFileToBlobStorage(string filename, string containerName, Stream file)
        {
            file.Position = 0;
            string Connection = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            var blobClient = new BlobContainerClient(Connection, containerName);
            await blobClient.CreateIfNotExistsAsync();
            var blob = blobClient.GetBlobClient(filename);
            if (!await blob.ExistsAsync())
            {
                await blob.UploadAsync(file);
            }
        }

        private static async Task<Item> ProcessItem(Item item, ILogger log)
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
            catch (Exception ex)
            {
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

            try
            {
                await FillFlagsAndModifiers(item);
            }
            catch (Exception ex)
            {
                log.LogError(ex, ex.Message + $"Item: {description}");
            }

            return item;
        }

        private static async Task FillFlagsAndModifiers(Item item)
        {
            for (var index = 0; index < item.Affects.Count; index++)
            {
                var line = item.Affects[index];
                if (line == "You find no hidden power within this object.")
                    continue;

                if (line.StartsWith("When worn, it protects you against"))
                {
                    index++;
                    var armorLine = line + item.Affects[index];
                    if(armorLine.EndsWith(','))
                    {
                        index++;
                        armorLine += item.Affects[index];
                    }
                    var match = numRegex.Match(armorLine);
                    if (match.Success)
                    {
                        item.Pierce = match.Groups[1].Value;
                        item.Bash = match.Groups[2].Value;
                        item.Slash = match.Groups[3].Value;
                        item.Magic = match.Groups[4].Value;
                        item.Element = match.Groups[5].Value;
                        continue;
                    }
                }

                var flag = line switch
                {
                    var str when str.Contains("It radiates light.") => "glowing",
                    var str when str.Contains("It emanates sound.") => "humming",
                    var str when str.Contains("A magical aura surrounds it.") => "magic",
                    var str when str.Contains("An orderly aura surrounds it.") => "orderly",
                    var str when str.Contains("A chaotic, uneven aura surrounds it.") => "chaotic",
                    var str when str.Contains("It is transparent.") => "transparant",
                    var str when str.Contains("It can't be removed.") => "cursed",
                    var str when str.Contains("It can't be dropped with ease.") => "no_drop",
                    var str when str.Contains("It is unusable for those of a pure soul.") => "anti_good",
                    var str when str.Contains("Those with a balanced soul cannot use it.") => "anti_neutral",
                    var str when str.Contains("People of a dark heart cannot use it.") => "anti_evil",
                    var str when str.Contains("It is easily concealed.") => "hidden",
                    var str when str.Contains("It has been imbued with a blessing.") => "bless",
                    var str when str.Contains("It has a chilling aura of evil.") => "evil",
                    var str when str.Contains("It shines with a pure, goodly aura.") => "good",
                    var str when str.Contains("It seems to be dark and cloaked in shadows.") => "dark",
                    var str when str.Contains("A thief, no one else, could use it.") => "thief_only",
                    var str when str.Contains("Only a dwarf could possibly use it.") => "dwarf_only",
                    var str when str.Contains("It is meant for a woman.") => "female_only",
                    var str when str.Contains("Only those of chaotic nature could use it.") => "chaotic_only",
                    var str when str.Contains("Upon death, it will crumble.") => "rot_death",
                    var str when str.Contains("It is invisible to the plain eye.") => "hideden",
                    var str when str.Contains("Only a ranger could utilize it.") => "ranger_only",
                    var str when str.Contains("Only a student of the arcane arts could use it.") => "conjurer_only",
                    var str when str.Contains("Only an elf could put it to use.") => "elf_only",
                    var str when str.Contains("Only a conjurer could use it.") => "conjurer_only",
                    var str when str.Contains("It seems to be made for a gnome to use.") => "gnome_only",
                    var str when str.Contains("Only a half-elf could use it.") => "halfelf_only",
                    var str when str.Contains("It is clearly meant for a giant.") => "giant_only",
                    var str when str.Contains("It obviously is meant for an arial.") => "arial_only",
                    var str when str.Contains("It appears to be made for those of small stature.") => "small_only",
                    var str when str.Contains("It appears to be made for a felar.") => "felar_only",
                    var str when str.Contains("It appears to be made for a holy priest.") => "shaman_only",
                    var str when str.Contains("It is meant for a shaman.") => "shaman_only",
                    var str when str.Contains("It is usable only by the Blood Tribunals.") => "trib_only",
                    var str when str.Contains("It seems to be made for a transmuter.") => "transmuter_only",
                    var str when str.Contains("It seems to be made for an orc.") => "orc_only",
                    var str when str.Contains("It seems to require the skills of a Herald to be used.") => "herald_only",
                    var str when str.Contains("Only a human could make use of it.") => "human_only",
                    var str when str.Contains("Only an archer could utilize it.") => "archer_only",
                    var str when str.Contains("Only an Imperial Citizen could put it to use.") => "empire_only",
                    var str when str.Contains("Only those of orderly nature could use it.") => "orderly_only",
                    var str when str.Contains("It appears to be made for a true paladin.") => "paladin_only",
                    var str when str.Contains("It appears to have been made for a druid.") => "druid_only",
                    var str when str.Contains("It has clearly been meant for a giant.") => "giant_only",
                    var str when str.Contains("It is for those who understand the balance between law and chaos.") => "neutral_only",
                    var str when str.Contains("It is imbued with a terrible, unholy blessing.") => "unholy",
                    var str when str.Contains("It is immune to normal attempts of disarming.") => "no_disarm",
                    var str when str.Contains("It is meant for a bard or a minstrel.") => "bard_only",
                    var str when str.Contains("It is meant for a healer.") => "healer_only",
                    var str when str.Contains("It is meant for a man.") => "man_only",
                    var str when str.Contains("It is only for the Seekers of Balance.") => "Nexus_only",
                    var str when str.Contains("It is usable only by a true Battle Rager.") => "Battle_only",
                    var str when str.Contains("It seems to be made for a shapeshifter.") => "shifter_only",
                    var str when str.Contains("Only a Master of the Five Magics could use it.") => "Masters_only",
                    var str when str.Contains("Only a necromancer could utilize it.") => "necro_only",
                    var str when str.Contains("Only a stealthy assassin could use it.") => "assassin_only",
                    var str when str.Contains("Only a true warrior could put it to use.") => "warrior_only",
                    var str when str.Contains("Only an anti-paladin could put it to use.") => "anti-paladin_only",
                    var str when str.Contains("Only an invoker could put it to use.") => "invoker_only",
                    var str when str.Contains("Only an Outlander of Thar'Eris could use it.") => "Outlander_only",
                    var str when str.Contains("Only one of the Holy Brigade of the Phoenix could use it.") => "Fortress_only",
                    _ => string.Empty
                };

                if (string.IsNullOrEmpty(flag))
                {
                    // Magic information
                    if(line.StartsWith("Within it are contained ") || line.StartsWith("Within it is contained "))
                    {
                        var holeLine = line;
                        if (!line.EndsWith('.'))
                        {
                            index++;
                            holeLine = line + item.Affects[index];
                        }

                        var matches = spellsRegex.Matches(holeLine);
                        if (matches.Any())
                        {
                            item.Spell = string.Join(',', matches.Select(x => x.Groups[0].Value));
                        }

                        item.SpellLevel = numRegex.Match(holeLine).Value;
                        item.MagicAffects.Add(holeLine);
                        continue;
                    }
                    if(line.StartsWith("It can be used"))
                    {
                        item.MagicAffects.Add(line.Split('.').First());
                        if(line.Contains("and contains the spell"))
                        {
                            var matched = wandRegex.Match(line);
                            if (matched.Success)
                            {
                                item.Spell = matched.Groups[1].Value;
                                item.SpellLevel = matched.Groups[2].Value;
                            }
                        }
                        continue;
                    }

                    var match = wandRegex.Match(line);
                    if (match.Success)
                    {
                        item.Spell = match.Groups[1].Value;
                        item.SpellLevel = match.Groups[2].Value;
                        item.MagicAffects.Add(line);
                        continue;
                    }

                    if (!await FillModifier(line, item))
                    {
                        await _tableService.MissingMapping(line, "FillFlags", fileName);
                    }
                }
                else
                {
                    item.Flaggs.Add(flag);
                }
            }

            if (item.Flaggs != null && item.Flaggs.Any())
            {
                item.FlaggsPiped = string.Join('|', item.Flaggs);
            }

            if (item.Modifiers != null && item.Modifiers.Any())
            {
                item.ModifiersPiped = string.Join('|', item.Modifiers);
            }

            if (item.MagicAffects != null && item.MagicAffects.Any())
            {
                item.MagicAffectsPiped = string.Join('|', item.MagicAffects);
            }
        }

        private static async Task<bool> FillModifier(string line, Item item)
        {
            var matches = modifierRegex.Matches(line);
            foreach (Match match in matches)
            {
                if (match.Success)
                {
                    var type = match.Groups[1];
                    var value = match.Groups[2];
                    item.Modifiers.Add($"Modifies {type} by {value}");
                    switch (type.Value)
                    {
                        case "hit roll":
                            item.Hit = value.Value;
                            break;
                        case "damage roll":
                            item.Dam = value.Value;
                            break;
                        case "hp":
                            item.Hp = value.Value;
                            break;
                        case "mana":
                            item.Mana = value.Value;
                            break;
                        case "moves":
                            item.Moves = value.Value;
                            break;
                        case "strength":
                            item.Str = value.Value;
                            break;
                        case "intelligence":
                            item.Int = value.Value;
                            break;
                        case "wisdom":
                            item.Wis = value.Value;
                            break;
                        case "dexterity":
                            item.Dex = value.Value;
                            break;
                        case "constitution":
                            item.Con = value.Value;
                            break;
                        case "charisma":
                            item.Chr = value.Value;
                            break;
                        case "save vs spell":
                            item.Svs = value.Value;
                            break;
                        case "save vs paralysis":
                            item.Svp = value.Value;
                            break;
                        case "save vs breath":
                            item.Svb = value.Value;
                            break;
                        case "save vs mental":
                            item.Svm = value.Value;
                            break;
                        case "armor class":
                            item.Ac = value.Value;
                            break;
                        case "age":
                            item.Age = value.Value;
                            break;
                        case "morale":
                            item.Morale = value.Value;
                            break;
                        default:
                            break;
                    }
                }
                else
                {
                    if (line.Contains("your ability to land accurate blows"))
                    {
                        // When worn, it affects your ability to land accurate blows by 5 points.
                        //your ability to land accurate blows by 1 points.
                        var value = line.Replace(" points.", "").Split(' ').Last();
                        if(!string.IsNullOrEmpty(value))
                        {
                            item.Hit = value;
                        }

                        item.Modifiers.Add(line);
                    }
                    else if (line.StartsWith("your ability to resist breath spells"))
                    {
                        //your ability to resist breath spells by -10 points.
                        var value = line.Replace("your ability to resist breath spells by ", "").Split(' ')[0];
                        if (!string.IsNullOrEmpty(value))
                        {
                            item.Svb = value;
                        }

                        item.Modifiers.Add(line);
                    }
                    else if(line.StartsWith("Your divination reveals that"))
                    {
                        item.Modifiers.Add(line);
                    }
                    else
                    {
                        await _tableService.MissingMapping(line, "FillModifier", fileName);
                    }
                }
            }

            return matches.Any();
        }

        private static void FillAffects(Item item)
        {
            var madeOfIndex = item.Data.FindIndex(x => x.StartsWith("It is made of ")) + 1;
            if (item.IsWeapon)
            {
                madeOfIndex = madeOfIndex + 1;
            }

            item.Affects = item.Data.Skip(madeOfIndex).ToList();
            item.AffectsPiped = string.Join('|', item.Affects);
        }

        private static void FillMaterialAndWeight(Item item)
        {
            var input = item.Data.Where(x => x.Contains("It is made of ")).FirstOrDefault();
            if (input != null)
            {
                var parts = input.Split(' ');
                item.Material = parts[4];
                item.Weight = $"{parts[7]},{parts[9]}";

                if (input.Contains("pounds"))
                {
                    var pounds = double.Parse(parts[7]);
                    if (pounds != 0)
                    {
                        var kg = Math.Abs(pounds * 0.45359237).ToString();
                        item.Weight = kg.Substring(0, kg.IndexOf(",") + 4);
                    }
                    else
                    {
                        item.Weight = "0";
                    }
                }

                item.Kg = item.Weight.Split(',').First();
                item.Gram = item.Weight.Split(',').Last();
            }
        }

        private static void FillGroupAndType(Item item)
        {
            var madeOfIndex = item.Data.FindIndex(x => x.StartsWith("It is made of "));
            var groupAndType = ("unknow", "unknow");
            if (madeOfIndex != -1)
            {
                groupAndType = item.Data.ElementAt(madeOfIndex - 1) switch
                {
                    "It is a talisman" => ("talisman", "talisman"),
                    var str when str.StartsWith("It is armor worn on the body.") => ("armor", "body"),
                    var str when str.StartsWith("It is armor worn about the body.") => ("armor", "about"),
                    var str when str.StartsWith("It is armor") => ("armor", str.Split(" ").Last().TrimEnd('.')),
                    var str when str.StartsWith("It is clothing worn on the body.") => ("clothing", "body"),
                    var str when str.StartsWith("It is clothing worn about the body.") => ("clothing", "about"),
                    var str when str.StartsWith("It is clothing") => ("clothing", str.Split(" ").Last().TrimEnd('.')),
                    var str when str.Contains("with an attack type") => ("weapon", str.Split(" ")[3].TrimEnd('.')),
                    var str when str.StartsWith("It is a") || str.StartsWith("It is an") => (str.Split(" ").Last().TrimEnd('.'), str.Split(" ").Last().TrimEnd('.')),
                    _ => ("unknow", "unknow")
                };
            }

            if(groupAndType.Item1 == "unknow") // get group from title
            {
                var description = string.Join(' ', item.Data);
                string[] parts = description.Split(" can be referred to as ", StringSplitOptions.RemoveEmptyEntries);
                var name = parts[0];
                if (name.Contains(", "))
                {
                    name = name.Split(',')[0].Split(' ')[1];
                }

                if(name == "object" && item.FullDataPiped.Contains("average"))
                {
                    name = "weapon";
                }

                groupAndType.Item1 = name;
            }

            item.Group = groupAndType.Item1;

            if(groupAndType.Item2 == "unknow")
            {
                var firstKeyword = groupAndType.Item2;
                var description = string.Join(' ', item.Data);
                firstKeyword = description.Split(" can be referred to as '", StringSplitOptions.RemoveEmptyEntries)[1].Split(' ')[0];
                groupAndType.Item2 = firstKeyword;
            }

            item.Type = groupAndType.Item2;

            //weapon extra info
            var averageString = item.Data.Where(x => x.StartsWith("It can cause ")).FirstOrDefault();
            if(!string.IsNullOrEmpty(averageString))
            {
                item.Avg = averageString.Split(" ").Last().TrimEnd('.');
            }

            if (item.IsWeapon && madeOfIndex != -1)
            {
                var damnounStringParts = item.Data.ElementAt(madeOfIndex - 1).Split(" ");
                var damnoun = damnounStringParts.Last().TrimEnd('.');
                if (damnounStringParts[damnounStringParts.Length - 2] != "of")
                {
                    damnoun = $"{damnounStringParts[damnounStringParts.Length - 2]} {damnoun}";
                }

                item.Damnoun = damnoun;
            }
        }

        private static string ExtractItemName(string description)
        {
            string[] parts = description.Split(" can be referred to as ", StringSplitOptions.RemoveEmptyEntries);
            var name = parts[0];
            if(name.Contains(", "))
            {
                name = name.Split(',')[1].Trim();
            }

            return name;
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
            var worthString = "0";
            if(match.Success)
            {
                worthString = match.Groups[1].Value;
            }
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
