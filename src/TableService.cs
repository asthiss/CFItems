using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace CFItems
{
    public interface ITableService
    {
        Task MissingMapping(string value, string function, string fileName);
        Task InsertItem(Item item);
        IEnumerable<Item> GetAllItemsAsync();

    }
    public class TableService : ITableService
    {
        private readonly TableClient _tableClient;
        private readonly TableClient _tableClientMissingData;

        public TableService(string connectionString, string tableName)
        {
            _tableClient = new TableClient(connectionString, tableName);
            _tableClient.CreateIfNotExistsAsync();
            _tableClientMissingData = new TableClient(connectionString, "missingdata");
            _tableClientMissingData.CreateIfNotExistsAsync();
        }

        public TableService(Uri endpoint)
        {
            _tableClient = new TableClient(endpoint);
            _tableClient.CreateIfNotExistsAsync();
        }

        public async Task InsertItem(Item item)
        {
            await _tableClient.UpsertEntityAsync(item);
        }
        public async Task MissingMapping(string value, string function, string fileName)
        {
            await _tableClientMissingData.AddEntityAsync(new MissingMapping
            {
                Value = value,
                Function = function,
                FileName = fileName
            });
        }

        public IEnumerable<Item> GetAllItemsAsync()
        {
            var result = new List<Item>();
            var items = _tableClient.Query<Item>();
            foreach (var item in items)
            {
                result.Add(item);
            }

            return result;
        }
    }

    public class MissingMapping : ITableEntity
    {
        public MissingMapping()
        {
            PartitionKey = DateTime.Now.ToString("yyyy-MM");
            RowKey = Guid.NewGuid().ToString();
        }
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public string Function { get; set; }
        public string Value { get; set; }
        public string FileName { get; set; }
    }


    public class Item : ITableEntity
    {
        public Item()
        {
            PartitionKey = "CFItems";
            Data = new List<string>();
            Affects = new List<string>();
            MagicAffects = new List<string>();
            Flaggs = new List<string>();
            Modifiers = new List<string>();
        }
        public string Name { get; set; }
        public string Level { get; set; }
        public int Worth { get; set; }
        public string Type { get; set; }
        public string Group { get; set; }
        public string Damnoun { get; set; }
        public string Weight { get; set; }
        public string Kg { get; set; }
        public string Gram { get; set; }
        public string Material { get; set; }
        [IgnoreDataMember]
        public List<string> Data { get; set; }
        public string FullDataPiped { get; set; }

        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        [IgnoreDataMember]
        public List<string> Affects { get; internal set; }
        public string AffectsPiped { get; set; }
        [IgnoreDataMember]
        public List<string> MagicAffects { get; internal set; }
        public string MagicAffectsPiped { get; set; }
        public string Area { get; set; }
        public string Avg { get; set; }
        public bool IsWeapon => this.Group == "weapon";
        public bool IsMagic => new string[] { "pill", "potion", "wand", "talisman", "scroll" }.Contains(this.Group);
        [IgnoreDataMember]
        public List<string> Flaggs { get; set; }
        public string FlaggsPiped { get; set; }
        [IgnoreDataMember]
        public List<string> Modifiers { get; set; }
        public string ModifiersPiped { get; set; }
        public string Hit { get; set; }
        public string Dam { get; set; }
        public string Hp { get; set; }
        public string Mana { get; set; }
        public string Moves { get; set; }
        public string Str { get; set; }
        public string Int { get; set; }
        public string Wis { get; set; }
        public string Dex { get; set; }
        public string Con { get; set; }
        public string Chr { get; set; }
        public string Svs { get; set; }
        public string Svp { get; set; }
        public string Svb { get; set; }
        public string Svm { get; set; }
        public string Ac { get; set; }
        public string Pierce { get; set; }
        public string Bash { get; set; }
        public string Slash { get; set; }
        public string Magic { get; set; }
        public string Element { get; set; }
        public string Spell { get; set; }
        public string SpellLevel { get; set; }
        public string Age { get; set; }
        public string Morale { get; set; }
        public string ArmorLine { get; set; }
        public string BaseDamnoun { get; set; }
    }
}
