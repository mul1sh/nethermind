﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Cryptography.Ssz;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;
namespace Nethermind.BeaconNode.Test.Storage
{
    [TestClass]
    public class MemoryStoreProviderTest
    {
        private TestContext? _testContext;

        public TestContext TestContext
        {
            get => _testContext!;
            set => _testContext = value;
        }

        [TestMethod]
        public async Task HistoricalBlocksShouldBeStored()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = forkChoice.GetGenesisStore(state);

            MiscellaneousParameters miscellaneousParameters =
                testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            TimeParameters timeParameters =
                testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            StateListLengths stateListLengths =
                testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock =
                testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            // Move forward time
            ulong targetTime = 10 * 6; // part way into epoch 2
            for (ulong timeSinceGenesis = 1; timeSinceGenesis <= targetTime; timeSinceGenesis++)
            {
                ulong time = state.GenesisTime + timeSinceGenesis;
                await forkChoice.OnTickAsync(store, time);
                if (timeSinceGenesis % timeParameters.SecondsPerSlot == 0)
                {
                    BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
                    TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
                    await forkChoice.OnBlockAsync(store, block);
                }
            }
            
            TestContext.WriteLine("");
            TestContext.WriteLine("***** State advanced to slot {0}, time {1}, ready to start tests *****", state.Slot, store.Time);
            TestContext.WriteLine("");
            
            // Act
            IStoreProvider storeProvider = testServiceProvider.GetService<IStoreProvider>();
            storeProvider.ShouldBeOfType(typeof(MemoryStoreProvider));
            storeProvider.TryGetStore(out IStore? retrievedStore).ShouldBeTrue();
            retrievedStore!.ShouldBeOfType(typeof(MemoryStore));

            Hash32 headRoot = await forkChoice.GetHeadAsync(store);
            Hash32 block2Root = await forkChoice.GetAncestorAsync(store, headRoot, new Slot(2));
            Hash32 block1Root = await forkChoice.GetAncestorAsync(store, block2Root, Slot.One);
            Hash32 genesisRoot = await forkChoice.GetAncestorAsync(store, block1Root, Slot.Zero);

            BeaconBlock headBlock = await store.GetBlockAsync(headRoot);
            BeaconBlock block2 = await store.GetBlockAsync(block2Root);
            BeaconBlock block1 = await store.GetBlockAsync(block1Root);
            BeaconBlock genesisBlock = await store.GetBlockAsync(genesisRoot);

            BeaconState headState= await store.GetBlockStateAsync(headRoot);
            BeaconState block2State = await store.GetBlockStateAsync(block2Root);
            BeaconState block1State = await store.GetBlockStateAsync(block1Root);
            BeaconState genesisState = await store.GetBlockStateAsync(genesisRoot);

            TestContext.WriteLine("Genesis lookup root: {0}", genesisRoot);
            TestContext.WriteLine("Block 1 lookup root: {0}", block1Root);
            TestContext.WriteLine("Block 2 lookup root: {0}", block2Root);
            TestContext.WriteLine("");

            Hash32 genesisSigningRoot = cryptographyService.SigningRoot(genesisBlock!);
            Hash32 genesisHashTreeRoot = cryptographyService.HashTreeRoot(genesisBlock!);
            Hash32 block1SigningRoot = cryptographyService.SigningRoot(block1!);
            Hash32 block1HashTreeRoot = cryptographyService.HashTreeRoot(block1!);
            Hash32 block2SigningRoot = cryptographyService.SigningRoot(block2!);
            Hash32 block2HashTreeRoot = cryptographyService.HashTreeRoot(block2!);

            TestContext.WriteLine("Genesis sign root: {0}, hash root: {1}", genesisSigningRoot, genesisHashTreeRoot);
            TestContext.WriteLine("Block 1 sign root: {0}, hash root: {1}", block1SigningRoot, block1HashTreeRoot);
            TestContext.WriteLine("Block2 sign root: {0}, hash root: {1}", block2SigningRoot, block2HashTreeRoot);
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state root: {0}", genesisBlock!.StateRoot);
            TestContext.WriteLine("Block state 1 root: {0}", block1!.StateRoot);
            TestContext.WriteLine("Block state 2 root: {0}", block2!.StateRoot);
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state hash root: {0}",
                cryptographyService.HashTreeRoot(genesisState!));
            TestContext.WriteLine("State 1 hash root: {0}",
                cryptographyService.HashTreeRoot(block1State!));
            TestContext.WriteLine("State 2 hash root: {0}",
                cryptographyService.HashTreeRoot(block2State!));
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state last block root: {0}, latest block header parent {1}, signing root {2}",
                genesisState!.BlockRoots.Last(), genesisState!.LatestBlockHeader.ParentRoot, genesisState!.LatestBlockHeader.SigningRoot());
            TestContext.WriteLine("State 1 last block root: {0}, latest block header parent {1}, signing root {2}",
                block1State!.BlockRoots.Last(), block1State!.LatestBlockHeader.ParentRoot, block1State!.LatestBlockHeader.SigningRoot());
            TestContext.WriteLine("State 2 last block root: {0}, latest block header parent {1}, signing root {2}",
                block2State!.BlockRoots.Last(), block2State!.LatestBlockHeader.ParentRoot, block2State!.LatestBlockHeader.SigningRoot());
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state last state root: {0}, last historical root {1}",
                genesisState!.StateRoots.LastOrDefault(), genesisState!.HistoricalRoots.LastOrDefault());
            TestContext.WriteLine("State 1 last state root: {0}, last historical root {1}",
                block1State!.StateRoots.Last(), block1State!.HistoricalRoots.LastOrDefault());
            TestContext.WriteLine("State 2 state last state root: {0}, last historical root {1}",
                block2State!.StateRoots.Last(), block2State!.HistoricalRoots.LastOrDefault());
            TestContext.WriteLine("Head state last state root: {0}, last historical root {1}",
                headState!.StateRoots.Last(), headState!.HistoricalRoots.LastOrDefault());
            TestContext.WriteLine("");

            BeaconBlockHeader state1LatestHeader = block1State!.LatestBlockHeader;
            TestContext.WriteLine(
                "State 1 latest header, slot: {0}, parent {1}, body root {2}, state root {3}, signature {4}, signing root {5}",
                state1LatestHeader.Slot, state1LatestHeader.ParentRoot, state1LatestHeader.BodyRoot,
                state1LatestHeader.StateRoot, state1LatestHeader.Signature,
                state1LatestHeader.SigningRoot());
            
            Hash32 block1BodyHashTreeRootResult = cryptographyService.HashTreeRoot(block1!.Body);
            Hash32 block1SigningRootResult = cryptographyService.SigningRoot(block1!);
//            Hash32 block1HashTreeRootResult = cryptographyService.HashTreeRoot(block1!,
//                maxOperationsPerBlock.MaximumProposerSlashings,
//                maxOperationsPerBlock.MaximumAttesterSlashings, maxOperationsPerBlock.MaximumAttestations,
//                maxOperationsPerBlock.MaximumDeposits, maxOperationsPerBlock.MaximumVoluntaryExits,
//                miscellaneousParameters.MaximumValidatorsPerCommittee);

            TestContext.WriteLine(
                "-- compare to Block 1, slot: {0}, parent {1}, body root {2}, state root {3}, signature {4}, signing root {5}",
                block1!.Slot, block1!.ParentRoot, block1BodyHashTreeRootResult,
                block1!.StateRoot, block1!.Signature,
                block1SigningRootResult);

            // Assert
            block2!.ParentRoot.ShouldBe(block1Root);
            
            block1SigningRootResult.ShouldBe(block1Root);
            
            block1.ParentRoot.ToString().ShouldStartWith("0x89e36b63");
            block1.StateRoot.ToString().ShouldStartWith("0x35c6537a");
            block1SigningRootResult.ToString().ShouldStartWith("0xcdaa0640");
            block1State.LatestBlockHeader.BodyRoot.ToString().ShouldStartWith("0xaea12492");
        }
    }
}
