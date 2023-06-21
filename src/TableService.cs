﻿using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Xml;

namespace CFItems
{
    public interface ITableService
    {
        Task InsertItem(Item item);
        IEnumerable<Item> GetAllItemsAsync();

    }
    public class TableService : ITableService
    {
        private readonly TableClient _tableClient;
       
        public TableService(string connectionString, string tableName)
        {
            _tableClient = new TableClient(connectionString, tableName);
            _tableClient.CreateIfNotExistsAsync();
        }

        public TableService(Uri endpoint)
        {
            _tableClient = new TableClient(endpoint);
            _tableClient.CreateIfNotExistsAsync();
        }

        public async Task InsertItem(Item order)
        {
            await _tableClient.UpsertEntityAsync(order);
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



    public class Item : ITableEntity
    {
        public Item()
        {
            PartitionKey = DateTime.Now.ToString("yyyy-MM");
            Data = new List<string>();
        }
        public string Name { get; set; }
        public string Level { get; set; }
        public int Worth { get; set; }
        public string Type { get; set; }
        public string Group { get; set; }
        public string Damnoun { get; set; }
        public string Weight { get; set; }
        public string Material { get; set; }
        [IgnoreDataMember]
        public List<string> Data { get; set; }
        public string FullDataPiped { get; set; }

        public string FullData { get; set; }
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

    }
}
