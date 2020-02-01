//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.HashLib.Crypto.SHA3;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.AuRa
{
    public class AuRaValidatorProcessorFactory : IAuRaValidatorProcessorFactory
    {
        private const long DefaultStartBlockNumber = 1;

        private readonly IStateProvider _stateProvider;
        private readonly IAbiEncoder _abiEncoder;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IReadOnlyTransactionProcessorSource _readOnlyReadOnlyTransactionProcessorSource;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IValidatorStore _validatorStore;
        private readonly ILogManager _logManager;

        public AuRaValidatorProcessorFactory(IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IValidatorStore validatorStore,
            ILogManager logManager)
        {
            _stateProvider = stateProvider;
            _abiEncoder = abiEncoder;
            _transactionProcessor = transactionProcessor;
            _readOnlyReadOnlyTransactionProcessorSource = readOnlyTransactionProcessorSource;
            _blockTree = blockTree;
            _receiptStorage = receiptStorage;
            _validatorStore = validatorStore;
            _logManager = logManager;
        }

        public IAuRaValidatorProcessorExtension CreateValidatorProcessor(AuRaParameters.Validator validator, long? startBlock = null)
        {
            var auRaSealerValidator = new ValidSealerStrategy();
            long startBlockNumber = startBlock ?? DefaultStartBlockNumber;
            return validator.ValidatorType switch
            {
                AuRaParameters.ValidatorType.List => (IAuRaValidatorProcessorExtension) new ListBasedValidator(validator, auRaSealerValidator, _logManager),
                AuRaParameters.ValidatorType.Contract => new ContractBasedValidator(validator, _stateProvider, _abiEncoder, _transactionProcessor, _readOnlyReadOnlyTransactionProcessorSource, _blockTree, _receiptStorage, _validatorStore, auRaSealerValidator, _logManager, startBlockNumber),
                AuRaParameters.ValidatorType.ReportingContract => new ReportingContractBasedValidator(validator, _stateProvider, _abiEncoder, _transactionProcessor, _readOnlyReadOnlyTransactionProcessorSource, _blockTree, _receiptStorage, _validatorStore, auRaSealerValidator, _logManager, startBlockNumber),
                AuRaParameters.ValidatorType.Multi => new MultiValidator(validator, this, _blockTree, _validatorStore, _logManager),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }
}