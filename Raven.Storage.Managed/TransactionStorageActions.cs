//-----------------------------------------------------------------------
// <copyright file="TransactionStorageActions.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Raven.Database.Impl;
using Raven.Database.Plugins;
using Raven.Database.Storage;
using Raven.Http;
using Raven.Http.Exceptions;
using Raven.Json.Linq;
using Raven.Munin;
using Raven.Storage.Managed.Impl;
using System.Linq;
using Raven.Database.Json;

namespace Raven.Storage.Managed
{
    public class TransactionStorageActions : ITransactionStorageActions
    {
        private readonly TableStorage storage;
        private readonly IUuidGenerator generator;
        private readonly IEnumerable<AbstractDocumentCodec> documentCodecs;

        public TransactionStorageActions(TableStorage storage, IUuidGenerator generator, IEnumerable<AbstractDocumentCodec> documentCodecs)
        {
            this.storage = storage;
            this.generator = generator;
            this.documentCodecs = documentCodecs;
        }

		public Guid AddDocumentInTransaction(string key, Guid? etag, RavenJObject data, RavenJObject metadata, TransactionInformation transactionInformation)
		{
			var readResult = storage.Documents.Read(new RavenJObject {{"key", key}});
            if (readResult != null) // update
            {
                StorageHelper.AssertNotModifiedByAnotherTransaction(storage, this, key, readResult, transactionInformation);
				AssertValidEtag(key, readResult, storage.DocumentsModifiedByTransactions.Read(new RavenJObject { { "key", key } }), etag);

                readResult.Key["txId"] = transactionInformation.Id.ToByteArray();
                if (storage.Documents.UpdateKey(readResult.Key) == false)
                    throw new ConcurrencyException("PUT attempted on document '" + key +
                                                   "' that is currently being modified by another transaction");
            }
            else
            {
				readResult = storage.DocumentsModifiedByTransactions.Read(new RavenJObject { { "key", key } });
                StorageHelper.AssertNotModifiedByAnotherTransaction(storage, this, key, readResult, transactionInformation);
            }

			storage.Transactions.UpdateKey(new RavenJObject
			                               	{
			                               		{"txId", transactionInformation.Id.ToByteArray()},
			                               		{"timeout", DateTime.UtcNow.Add(transactionInformation.Timeout)}
			                               	});

            var ms = new MemoryStream();

            metadata.WriteTo(ms);
            var dataBytes = documentCodecs.Aggregate(data.ToBytes(), (bytes, codec) => codec.Encode(key, data, metadata, bytes));
            ms.Write(dataBytes, 0, dataBytes.Length);

            var newEtag = generator.CreateSequentialUuid();
			storage.DocumentsModifiedByTransactions.Put(new RavenJObject
			                                            	{
			                                            		{"key", key},
			                                            		{"etag", newEtag.ToByteArray()},
			                                            		{"modified", DateTime.UtcNow},
			                                            		{"txId", transactionInformation.Id.ToByteArray()}
			                                            	}, ms.ToArray());

            return newEtag;
        }

        private static void AssertValidEtag(string key, Table.ReadResult doc, Table.ReadResult docInTx, Guid? etag)
        {
            if (doc == null)
                return;
            var existingEtag =
                docInTx != null
                    ? new Guid(docInTx.Key.Value<byte[]>("etag"))
                    : new Guid(doc.Key.Value<byte[]>("etag"));


            if (etag != null && etag.Value != existingEtag)
            {
                throw new ConcurrencyException("PUT attempted on document '" + key +
                                               "' using a non current etag")
                {
                    ActualETag = etag.Value,
                    ExpectedETag = existingEtag
                };
            }
        }

        public void DeleteDocumentInTransaction(TransactionInformation transactionInformation, string key, Guid? etag)
        {
			var readResult = storage.Documents.Read(new RavenJObject { { "key", key } });
            if (readResult == null)
            {
                return;
            }
			readResult = storage.DocumentsModifiedByTransactions.Read(new RavenJObject { { "key", key } });
            StorageHelper.AssertNotModifiedByAnotherTransaction(storage, this, key, readResult, transactionInformation);
        	AssertValidEtag(key, readResult, storage.DocumentsModifiedByTransactions.Read(new RavenJObject {{"key", key}}),etag);

            if (readResult != null)
            {
                readResult.Key["txId"] = transactionInformation.Id.ToByteArray();
                if (storage.Documents.UpdateKey(readResult.Key) == false)
                    throw new ConcurrencyException("DELETE attempted on document '" + key +
                                                   "' that is currently being modified by another transaction");
            }

        	storage.Transactions.UpdateKey(new RavenJObject
        	                               	{
        	                               		{"txId", transactionInformation.Id.ToByteArray()},
        	                               		{"timeout", DateTime.UtcNow.Add(transactionInformation.Timeout)}
        	                               	});

            var newEtag = generator.CreateSequentialUuid();
        	storage.DocumentsModifiedByTransactions.UpdateKey(new RavenJObject
        	                                                  	{
        	                                                  		{"key", key},
        	                                                  		{"etag", newEtag.ToByteArray()},
        	                                                  		{"modified", DateTime.UtcNow},
        	                                                  		{"deleted", true},
        	                                                  		{"txId", transactionInformation.Id.ToByteArray()}
        	                                                  	});
        }

        public void RollbackTransaction(Guid txId)
        {
            CompleteTransaction(txId, data =>
            {
				var readResult = storage.Documents.Read(new RavenJObject { { "key", data.Key } });
                if (readResult == null)
                    return;
                ((RavenJObject)readResult.Key).Properties.Remove("txId");
                storage.Documents.UpdateKey(readResult.Key);
            });
        }

        public void ModifyTransactionId(Guid fromTxId, Guid toTxId, TimeSpan timeout)
        {
            storage.Transactions.UpdateKey(new RavenJObject
            {
				{"txId", toTxId.ToByteArray()},
                {"timeout", DateTime.UtcNow.Add(timeout)}
            });

            var transactionInformation = new TransactionInformation { Id = toTxId, Timeout = timeout };
            CompleteTransaction(fromTxId, data =>
            {
				var readResult = storage.Documents.Read(new RavenJObject { { "key", data.Key } });
                if (readResult != null)
                {
                    ((RavenJObject)readResult.Key)["txId"] = toTxId.ToByteArray();
                    storage.Documents.UpdateKey(readResult.Key);
                }

                if (data.Delete)
                    DeleteDocumentInTransaction(transactionInformation, data.Key, null);
                else
                    AddDocumentInTransaction(data.Key, null, data.Data, data.Metadata, transactionInformation);
            });
        }

        public void CompleteTransaction(Guid txId, Action<DocumentInTransactionData> perDocumentModified)
        {
        	storage.Transactions.Remove(new RavenJObject {{"txId", txId.ToByteArray()}});

            var documentsInTx = storage.DocumentsModifiedByTransactions["ByTxId"]
				.SkipTo(new RavenJObject {{"txId", txId.ToByteArray()}})
                .TakeWhile(x => new Guid(x.Value<byte[]>("txId")) == txId);

            foreach (var docInTx in documentsInTx)
            {
                var readResult = storage.DocumentsModifiedByTransactions.Read(docInTx);

                storage.DocumentsModifiedByTransactions.Remove(docInTx);

				RavenJObject metadata = null;
				RavenJObject data = null;
                if (readResult.Position > 0) // position can never be 0, because of the skip record
                {
                    var ms = new MemoryStream(readResult.Data());
                    metadata = ms.ToJObject();
                    data = ms.ToJObject();
                }
                perDocumentModified(new DocumentInTransactionData
                {
                    Key = readResult.Key.Value<string>("key"),
                    Etag = new Guid(readResult.Key.Value<byte[]>("etag")),
                    Delete = readResult.Key.Value<bool>("deleted"),
                    Metadata = metadata,
                    Data = data,
                });

            }
        }

        public IEnumerable<Guid> GetTransactionIds()
        {
            return storage.Transactions.Keys.Select(x => new Guid(x.Value<byte[]>("txId")));
        }
    }
}