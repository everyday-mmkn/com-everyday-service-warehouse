﻿using Com.Bateeq.Service.Warehouse.Lib.Helpers;
using Com.Bateeq.Service.Warehouse.Lib.Models.InventoryModel;
using Com.Bateeq.Service.Warehouse.Lib.Models.SPKDocsModel;
using Com.Bateeq.Service.Warehouse.Lib.Models.TransferModel;
using Com.Moonlay.Models;
using Com.Moonlay.NetCore.Lib;
using HashidsNet;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Com.Bateeq.Service.Warehouse.Lib.Facades.Stores
{
    public class TransferInStoreFacade
    {
        private string USER_AGENT = "Facade";

        private readonly WarehouseDbContext dbContext;
        private readonly DbSet<TransferInDoc> dbSet;
        private readonly DbSet<SPKDocs> dbSetSpk;
        private readonly IServiceProvider serviceProvider;
        private readonly DbSet<Inventory> dbSetInventory;
        private readonly DbSet<InventoryMovement> dbSetInventoryMovement;

        public TransferInStoreFacade(IServiceProvider serviceProvider, WarehouseDbContext dbContext)
        {
            this.serviceProvider = serviceProvider;
            this.dbContext = dbContext;
            this.dbSet = dbContext.Set<TransferInDoc>();
            this.dbSetInventory = dbContext.Set<Inventory>();
            this.dbSetSpk = dbContext.Set<SPKDocs>();
            this.dbSetInventoryMovement = dbContext.Set<InventoryMovement>();
        }

        public Tuple<List<TransferInDoc>, int, Dictionary<string, string>> Read(int Page = 1, int Size = 25, string Order = "{}", string Keyword = null, string Filter = "{}")
        {
            IQueryable<TransferInDoc> Query = this.dbSet.Include(m => m.Items).Where(x=>x.SourceCode.Contains("GDG."));

            List<string> searchAttributes = new List<string>()
            {
                "Code"
            };

            Query = QueryHelper<TransferInDoc>.ConfigureSearch(Query, searchAttributes, Keyword);

            Dictionary<string, string> FilterDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Filter);
            Query = QueryHelper<TransferInDoc>.ConfigureFilter(Query, FilterDictionary);

            Dictionary<string, string> OrderDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Order);
            Query = QueryHelper<TransferInDoc>.ConfigureOrder(Query, OrderDictionary);

            Pageable<TransferInDoc> pageable = new Pageable<TransferInDoc>(Query, Page - 1, Size);
            List<TransferInDoc> Data = pageable.Data.ToList();
            int TotalData = pageable.TotalCount;

            return Tuple.Create(Data, TotalData, OrderDictionary);
        }

        public Tuple<List<SPKDocs>, int, Dictionary<string, string>> ReadPending(int Page = 1, int Size = 25, string Order = "{}", string Keyword = null, string Filter = "{}")
        {
            IQueryable<SPKDocs> Query = this.dbSetSpk.Include(m => m.Items).Where(i=>i.IsDistributed == true && i.IsReceived == false);

            List<string> searchAttributes = new List<string>()
            {
                "Code"
            };

            foreach(var i in Query)
            {
                if (/*i.Reference != null || i.Reference != ""*/ !String.IsNullOrWhiteSpace(i.Reference) && i.Reference.Contains("RTT"))
                {
                    var transferout = dbContext.TransferOutDocs.Where(x => x.Code == i.Reference).FirstOrDefault();
                    if (transferout != null)
                    {
                        i.SourceId = transferout.SourceId;
                        i.SourceCode = transferout.SourceCode;
                        i.SourceName = transferout.SourceName;
                        i.DestinationId = transferout.DestinationId;
                        i.DestinationName = transferout.DestinationName;
                        i.DestinationCode = transferout.DestinationCode;
                    }
                    else
                    {
                        i.SourceId = 0;
                        i.SourceCode = "-";
                        i.SourceName = "-";
                        i.DestinationId = 0;
                        i.DestinationName = "-";
                        i.DestinationCode = "-";
                    }
                }
            }

            Query = QueryHelper<SPKDocs>.ConfigureSearch(Query, searchAttributes, Keyword);

            Dictionary<string, string> FilterDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Filter);
            Query = QueryHelper<SPKDocs>.ConfigureFilter(Query, FilterDictionary);

            Dictionary<string, string> OrderDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Order);
            Query = QueryHelper<SPKDocs>.ConfigureOrder(Query, OrderDictionary);

            Pageable<SPKDocs> pageable = new Pageable<SPKDocs>(Query, Page - 1, Size);
            List<SPKDocs> Data = pageable.Data.ToList();
            int TotalData = pageable.TotalCount;

            return Tuple.Create(Data, TotalData, OrderDictionary);
        }

        public TransferInDoc ReadById(int id)
        {
            var model = dbSet.Where(m => m.Id == id)
                 .Include(m => m.Items)
                 .FirstOrDefault();
            return model;
        }

        public string GenerateCode(string ModuleId)
        {
            var uid = ObjectId.GenerateNewId().ToString();
            var hashids = new Hashids(uid, 8, "ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890");
            var now = DateTime.Now;
            var begin = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
            var diff = (now - begin).Milliseconds;
            string code = String.Format("{0}/{1}/{2}", hashids.Encode(diff), ModuleId, DateTime.Now.ToString("MM/yyyy"));
            return code;
        }

        public async Task<int> Create(TransferInDoc model, string username, int clientTimeZoneOffset = 7)
        {
            int Created = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    string code = GenerateCode("EFR-TB/BBT");
                    model.Code = code;
                    var SPK = dbContext.SPKDocs.Where(x => x.PackingList == model.Reference).Single();
                    SPK.IsReceived = true;
                    var Id = SPK.Id;
                    EntityExtension.FlagForCreate(model, username, USER_AGENT);
                    foreach (var i in model.Items)
                    {
                        i.Id = 0;
                        EntityExtension.FlagForCreate(i, username, USER_AGENT);
                        var SPKItems = dbContext.SPKDocsItems.Where(x => x.ItemArticleRealizationOrder == i.ArticleRealizationOrder && x.ItemCode == i.ItemCode && i.ItemName == i.ItemName && x.SPKDocsId == Id).Single();
                        SPKItems.SendQuantity = i.Quantity;

                        var inven = dbContext.Inventories.Where(x => x.ItemCode == i.ItemCode).FirstOrDefault();
                        if (inven != null)
                        {
                            inven.StorageCode = model.DestinationCode;
                            inven.StorageId = model.DestinationId;
                            inven.StorageName = model.DestinationName;
                        }
                    }

                    dbSet.Add(model);
                    Created = await dbContext.SaveChangesAsync();
                    transaction.Commit();
                }
                catch (Exception e)
                {
                    transaction.Rollback();
                    throw new Exception(e.Message);
                }
            }

            return Created;
        }




    }
}
