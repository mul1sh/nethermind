﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Nethermind.Core;
using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;
using RocksDbSharp;

namespace Nethermind.Db
{
    public abstract class DbOnTheRocks : IDb, IDbWithSpan
    {
        private static readonly ConcurrentDictionary<string, RocksDb> DbsByPath = new ConcurrentDictionary<string, RocksDb>();
        internal  readonly RocksDb Db;
        internal WriteBatch CurrentBatch;
        internal WriteOptions WriteOptions;

        public abstract string Name { get; }

        private static long _maxRocksSize;
        
        private long _maxThisDbSize;

        public DbOnTheRocks(string basePath, string dbPath, IDbConfig dbConfig, ILogManager logManager = null) // TODO: check column families
        {
            string fullPath = dbPath.GetApplicationResourcePath(basePath);
            _logger = logManager?.GetClassLogger() ?? NullLogger.Instance;
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }

            try
            {
                // ReSharper disable once VirtualMemberCallInConstructor
                if (_logger.IsDebug) _logger.Debug($"Building options for {Name} DB");
                DbOptions options = BuildOptions(dbConfig);
                
                // ReSharper disable once VirtualMemberCallInConstructor
                if (_logger.IsInfo) _logger.Info($"Loading {Name.PadRight(16)} from {fullPath} with max memory footprint of {_maxThisDbSize / 1024 / 1024}MB");
                Db = DbsByPath.GetOrAdd(fullPath, path => RocksDb.Open(options, path));
            }
            catch (DllNotFoundException e) when (e.Message.Contains("libdl"))
            {
                throw new ApplicationException($"Unable to load 'libdl' necessary to init the RocksDB database. Please run{Environment.NewLine}" +
                                               "sudo apt update && sudo apt install libsnappy-dev libc6-dev libc6");
            }
        }

        internal virtual void UpdateReadMetrics() => Metrics.OtherDbReads++;
        internal virtual void UpdateWriteMetrics() => Metrics.OtherDbWrites++;

        private T ReadConfig<T>(IDbConfig dbConfig, string propertyName)
        {
            string prefixed = string.Concat(Name == "State" ? string.Empty : string.Concat(Name, "Db"),
                propertyName);
            try
            {
                return (T) dbConfig.GetType().GetProperty(prefixed, BindingFlags.Public | BindingFlags.Instance)
                    .GetValue(dbConfig);
            }
            catch (Exception e)
            {
                throw new InvalidDataException($"Unable to read {prefixed} property from DB config", e);
            }
        }

        private DbOptions BuildOptions(IDbConfig dbConfig)
        {
            _maxThisDbSize = 0;
            BlockBasedTableOptions tableOptions = new BlockBasedTableOptions();
            tableOptions.SetBlockSize(16 * 1024);
            tableOptions.SetPinL0FilterAndIndexBlocksInCache(true);
            tableOptions.SetCacheIndexAndFilterBlocks(ReadConfig<bool>(dbConfig, nameof(dbConfig.CacheIndexAndFilterBlocks)));

            tableOptions.SetFilterPolicy(BloomFilterPolicy.Create(10, true));
            tableOptions.SetFormatVersion(2);

            ulong blockCacheSize = ReadConfig<ulong>(dbConfig, nameof(dbConfig.BlockCacheSize));
            _maxThisDbSize += (long) blockCacheSize;

            IntPtr cache = Native.Instance.rocksdb_cache_create_lru(new UIntPtr(blockCacheSize));
            tableOptions.SetBlockCache(cache);

            DbOptions options = new DbOptions();
            options.SetCreateIfMissing(true);
            options.SetAdviseRandomOnOpen(true);
            options.OptimizeForPointLookup(blockCacheSize); // I guess this should be the one option controlled by the DB size property - bind it to LRU cache size
            //options.SetCompression(CompressionTypeEnum.rocksdb_snappy_compression);
            //options.SetLevelCompactionDynamicLevelBytes(true);

            /*
             * Multi-Threaded Compactions
             * Compactions are needed to remove multiple copies of the same key that may occur if an application overwrites an existing key. Compactions also process deletions of keys. Compactions may occur in multiple threads if configured appropriately.
             * The entire database is stored in a set of sstfiles. When a memtable is full, its content is written out to a file in Level-0 (L0). RocksDB removes duplicate and overwritten keys in the memtable when it is flushed to a file in L0. Some files are periodically read in and merged to form larger files - this is called compaction.
             * The overall write throughput of an LSM database directly depends on the speed at which compactions can occur, especially when the data is stored in fast storage like SSD or RAM. RocksDB may be configured to issue concurrent compaction requests from multiple threads. It is observed that sustained write rates may increase by as much as a factor of 10 with multi-threaded compaction when the database is on SSDs, as compared to single-threaded compactions.
             * TKS: Observed 500MB/s compared to ~100MB/s between multithreaded and single thread compactions on my machine (processor count is returning 12 for 6 cores with hyperthreading)
             * TKS: CPU goes to insane 30% usage on idle - compacting only app
             */
            options.SetMaxBackgroundCompactions(Environment.ProcessorCount);

            //options.SetMaxOpenFiles(32);
            ulong writeBufferSize = ReadConfig<ulong>(dbConfig, nameof(dbConfig.WriteBufferSize));
            options.SetWriteBufferSize(writeBufferSize);
            int writeBufferNumber = (int) ReadConfig<uint>(dbConfig, nameof(dbConfig.WriteBufferNumber));
            options.SetMaxWriteBufferNumber(writeBufferNumber);
            options.SetMinWriteBufferNumberToMerge(2);
            
            lock (DbsByPath)
            {
                _maxThisDbSize += (long) writeBufferSize * writeBufferNumber;
                Interlocked.Add(ref _maxRocksSize, _maxThisDbSize);
                if (_logger.IsDebug) _logger.Debug($"Expected max memory footprint of {Name} DB is {_maxThisDbSize / 1024 / 1024}MB ({writeBufferNumber} * {writeBufferSize / 1024 / 1024}MB + {blockCacheSize / 1024 / 1024}MB)");
                if (_logger.IsDebug) _logger.Debug($"Total max DB footprint so far is {_maxRocksSize / 1024 / 1024}MB");
                ThisNodeInfo.AddInfo("DB mem est   :", $"{_maxRocksSize / 1024 / 1024}MB");
            }

            options.SetBlockBasedTableFactory(tableOptions);

            options.SetMaxBackgroundFlushes(Environment.ProcessorCount);
            options.IncreaseParallelism(Environment.ProcessorCount);
            options.SetRecycleLogFileNum(dbConfig.RecycleLogFileNum); // potential optimization for reusing allocated log files

//            options.SetLevelCompactionDynamicLevelBytes(true); // only switch on on empty DBs
            WriteOptions = new WriteOptions();
            WriteOptions.SetSync(dbConfig.WriteAheadLogSync); // potential fix for corruption on hard process termination, may cause performance degradation

            return options;
        }

        public byte[] this[byte[] key]
        {
            get
            {
                UpdateReadMetrics();
                return Db.Get(key);
            }
            set
            {
                UpdateWriteMetrics();
                if (CurrentBatch != null)
                {
                    if (value == null)
                    {
                        CurrentBatch.Delete(key);
                    }
                    else
                    {
                        CurrentBatch.Put(key, value);
                    }
                }
                else
                {
                    if (value == null)
                    {
                        Db.Remove(key, null, WriteOptions);
                    }
                    else
                    {
                        Db.Put(key, value, null, WriteOptions);
                    }
                }
            }
        }

        public Span<byte> GetSpan(byte[] key)
        {
            UpdateReadMetrics();
            return Db.GetSpan(key);
        }

        public void DangerousReleaseMemory(in Span<byte> span)
        {
            Db.DangerousReleaseMemory(in span);
        }

        public void Remove(byte[] key)
        {
            Db.Remove(key, null, WriteOptions);
        }

        public byte[][] GetAll()
        {
            Iterator iterator = Db.NewIterator();
            iterator = iterator.SeekToFirst();
            var values = new List<byte[]>();
            while (iterator.Valid())
            {
                values.Add(iterator.Value());
                iterator = iterator.Next();
            }

            iterator.Dispose();

            return values.ToArray();
        }

        private ILogger _logger;

        public bool KeyExists(byte[] key)
        {
            // seems it has no performance impact
            return Db.Get(key) != null;
//            return _db.Get(key, 32, _keyExistsBuffer, 0, 0, null, null) != -1;
        }

        public void StartBatch()
        {
            CurrentBatch = new WriteBatch();
        }

        public void CommitBatch()
        {
            Db.Write(CurrentBatch, WriteOptions);
            CurrentBatch.Dispose();
            CurrentBatch = null;
        }

        public void Dispose()
        {
            Db?.Dispose();
            CurrentBatch?.Dispose();
        }
    }
}