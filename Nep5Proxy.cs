using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System;
using System.ComponentModel;
using System.Numerics;

[assembly: Features(ContractPropertyState.HasStorage | ContractPropertyState.HasDynamicInvoke | ContractPropertyState.Payable)]

namespace Nep5Proxy
{
    public class Nep5Proxy : SmartContract
    {
        // Constants
        private static readonly byte[] CCMCScriptHash = "".HexToBytes(); // little endian
        private static readonly byte[] Operator = "".ToScriptHash(); // Operator address

        // Dynamic Call
        private delegate object DynCall(string method, object[] args); // dynamic call
        
        // Events
        public static event Action<byte[], byte[], BigInteger, byte[], byte[], BigInteger> LockEvent;
        public static event Action<byte[], byte[], BigInteger> UnlockEvent;
        public static event Action<byte[], BigInteger, byte[], BigInteger> BindAssetHashEvent;
        public static event Action<BigInteger, byte[]> BindProxyHashEvent;

        // Storage prefix
        private static readonly byte[] FromAssetListPrefix = new byte[] { 0x01, 0x01 }; // "FromAssetList";

        public static object Main(string method, object[] args)
        {
            if (Runtime.Trigger == TriggerType.Application)
            {
                var callscript = ExecutionEngine.CallingScriptHash;

                if (method == "bindProxyHash")
                    return BindProxyHash((BigInteger)args[0], (byte[])args[1]);
                if (method == "bindAssetHash")
                    return BindAssetHash((byte[])args[0], (BigInteger)args[1], (byte[])args[2]);
                if (method == "getAssetBalance")
                    return GetAssetBalance((byte[])args[0]);
                if (method == "getProxyHash")
                    return GetProxyHash((BigInteger)args[0]);
                if (method == "getAssetHash")
                    return GetAssetHash((byte[])args[0], (BigInteger)args[1]);
                if (method == "lock")
                    return Lock((byte[])args[0], (byte[])args[1], (BigInteger)args[2], (byte[])args[3], (BigInteger)args[4]);
                if (method == "unlock")
                    return Unlock((byte[])args[0], (byte[])args[1], (BigInteger)args[2], callscript);
                
                if (method == "upgrade")
                {
                    Runtime.Notify("In upgrade");
                    if (args.Length < 9) return false;
                    byte[] script = (byte[])args[0];
                    byte[] plist = (byte[])args[1];
                    byte rtype = (byte)args[2];
                    ContractPropertyState cps = (ContractPropertyState)args[3];
                    string name = (string)args[4];
                    string version = (string)args[5];
                    string author = (string)args[6];
                    string email = (string)args[7];
                    string description = (string)args[8];
                    return Upgrade(script, plist, rtype, cps, name, version, author, email, description);
                }
            }
            return false;
        }

        // add target proxy contract hash according to chain id into contract storage
        [DisplayName("bindProxyHash")]
        public static bool BindProxyHash(BigInteger toChainId, byte[] targetProxyHash)
        {
            if (!Runtime.CheckWitness(Operator)) return false;
            StorageMap proxyHash = Storage.CurrentContext.CreateMap(nameof(proxyHash));
            proxyHash.Put(toChainId.AsByteArray(), targetProxyHash);
            BindProxyHashEvent(toChainId, targetProxyHash);
            return true;
        }

        // add target asset contract hash according to local asset hash & chain id into contract storage
        [DisplayName("bindAssetHash")]
        public static bool BindAssetHash(byte[] fromAssetHash, BigInteger toChainId, byte[] toAssetHash)
        {
            if (!Runtime.CheckWitness(Operator)) return false;
            if (!AddFromAssetHash(fromAssetHash)) return false;

            StorageMap assetHash = Storage.CurrentContext.CreateMap(nameof(assetHash));
            assetHash.Put(fromAssetHash.Concat(toChainId.AsByteArray()), toAssetHash);

            BigInteger currentBalance = GetAssetBalance(fromAssetHash);
            BindAssetHashEvent(fromAssetHash, toChainId, toAssetHash, currentBalance);
            return true;
        }

        private static bool AddFromAssetHash(byte[] fromAssetHash)
        {
            StorageMap fromAssetList = Storage.CurrentContext.CreateMap(nameof(fromAssetList));
            byte[] info = fromAssetList.Get(FromAssetListPrefix.Concat(fromAssetHash));
            if (info.Length != 0)
            {
                Runtime.Notify("From asset hash exists");
                return true;
            }
            fromAssetList.Put(FromAssetListPrefix.Concat(fromAssetHash), fromAssetHash);
            return true;
        }

        [DisplayName("getAssetBalance")]
        public static BigInteger GetAssetBalance(byte[] assetHash)
        {
            byte[] currentHash = ExecutionEngine.ExecutingScriptHash; // this proxy contract hash
            var nep5Contract = (DynCall)assetHash.ToDelegate();
            BigInteger balance = (BigInteger)nep5Contract("balanceOf", new object[] { currentHash });
            return balance;
        }

        // get target proxy contract hash according to chain id
        [DisplayName("getProxyHash")]
        public static byte[] GetProxyHash(BigInteger toChainId)
        {
            StorageMap proxyHash = Storage.CurrentContext.CreateMap(nameof(proxyHash));
            return proxyHash.Get(toChainId.AsByteArray());
        }

        // get target asset contract hash according to local asset hash & chain id
        [DisplayName("getAssetHash")]
        public static byte[] GetAssetHash(byte[] fromAssetHash, BigInteger toChainId)
        {
            StorageMap assetHash = Storage.CurrentContext.CreateMap(nameof(assetHash));
            return assetHash.Get(fromAssetHash.Concat(toChainId.AsByteArray()));
        }

        // used to lock asset into proxy contract
        [DisplayName("lock")]
        public static bool Lock(byte[] fromAssetHash, byte[] fromAddress, BigInteger toChainId, byte[] toAddress, BigInteger amount)
        {
            if (fromAssetHash.Length != 20)
            {
                Runtime.Notify("The parameter fromAssetHash SHOULD be 20-byte long.");
                return false;
            }
            if (fromAddress.Length != 20)
            {
                Runtime.Notify("The parameter fromAddress SHOULD be 20-byte long.");
                return false;
            }
            if (toAddress.Length == 0)
            {
                Runtime.Notify("The parameter toAddress SHOULD not be empty.");
                return false;
            }
            if (amount < 0)
            {
                Runtime.Notify("The parameter amount SHOULD not be less than 0.");
                return false;
            }
            
            // get the corresbonding asset on target chain
            var toAssetHash = GetAssetHash(fromAssetHash, toChainId);
            if (toAssetHash.Length == 0)
            {
                Runtime.Notify("Target chain asset hash not found.");
                return false;
            }

            // get the proxy contract on target chain
            var toProxyHash = GetProxyHash(toChainId);
            if (toProxyHash.Length == 0)
            {
                Runtime.Notify("Target chain proxy contract not found.");
                return false;
            }

            // transfer asset from fromAddress to proxy contract address, use dynamic call to call nep5 token's contract "transfer"
            byte[] currentHash = ExecutionEngine.ExecutingScriptHash; // this proxy contract hash
            var nep5Contract = (DynCall)fromAssetHash.ToDelegate();
            bool success = (bool)nep5Contract("transfer", new object[] { fromAddress, currentHash, amount });
            if (!success)
            {
                Runtime.Notify("Failed to transfer NEP5 token to proxy contract.");
                return false;
            }

            // construct args for proxy contract on target chain
            var inputArgs = SerializeArgs(toAssetHash, toAddress, amount);
            // construct params for CCMC 
            var param = new object[] { toChainId, toProxyHash, "unlock", inputArgs };
            // dynamic call CCMC
            var ccmc = (DynCall)CCMCScriptHash.ToDelegate();
            success = (bool)ccmc("CrossChain", param);
            if (!success)
            {
                Runtime.Notify("Failed to call CCMC.");
                return false;
            }

            LockEvent(fromAssetHash, fromAddress, toChainId, toAssetHash, toAddress, amount);

            return true;
        }

#if DEBUG
        [DisplayName("unlock")] //Only for ABI file
        public static bool Unlock(byte[] inputBytes, byte[] fromProxyContract, BigInteger fromChainId) => true;
#endif

        // Methods of actual execution, used to unlock asset from proxy contract
        private static bool Unlock(byte[] inputBytes, byte[] fromProxyContract, BigInteger fromChainId, byte[] caller)
        {
            //only allowed to be called by CCMC
            if (caller.AsBigInteger() != CCMCScriptHash.AsBigInteger())
            {
                Runtime.Notify("Only allowed to be called by CCMC");
                Runtime.Notify(caller);
                Runtime.Notify(CCMCScriptHash);
                return false;
            }

            byte[] storedProxy = GetProxyHash(fromChainId);
            
            // check the fromContract is stored, so we can trust it
            if (fromProxyContract.AsBigInteger() != storedProxy.AsBigInteger())
            {
                Runtime.Notify("From proxy contract not found.");
                Runtime.Notify(fromProxyContract);
                Runtime.Notify(fromChainId);
                Runtime.Notify(storedProxy);
                return false;
            }

            // parse the args bytes constructed in source chain proxy contract, passed by multi-chain
            object[] results = DeserializeArgs(inputBytes);
            var toAssetHash = (byte[])results[0];
            var toAddress = (byte[])results[1];
            var amount = (BigInteger)results[2];
            if (toAssetHash.Length != 20) 
            { 
                Runtime.Notify("ToChain Asset script hash SHOULD be 20-byte long.");
                return false; 
            }
            if (toAddress.Length != 20)
            {
                Runtime.Notify("ToChain Account address SHOULD be 20-byte long.");
                return false;
            }
            if (amount < 0)
            {
                Runtime.Notify("ToChain Amount SHOULD not be less than 0.");
                return false;
            }

            // transfer asset from proxy contract to toAddress
            byte[] currentHash = ExecutionEngine.ExecutingScriptHash; // this proxy contract hash
            var nep5Contract = (DynCall)toAssetHash.ToDelegate();
            bool success = (bool)nep5Contract("transfer", new object[] { currentHash, toAddress, amount });
            if (!success)
            {
                Runtime.Notify("Failed to transfer NEP5 token to toAddress.");
                return false;
            }

            UnlockEvent(toAssetHash, toAddress, amount);

            return true;
        }
        
        // used to upgrade this proxy contract
        [DisplayName("upgrade")]
        public static bool Upgrade(byte[] newScript, byte[] paramList, byte returnType, ContractPropertyState cps, string name, string version, string author, string email, string description)
        {
            if (!Runtime.CheckWitness(Operator)) return false;
            var contract = Contract.Migrate(newScript, paramList, returnType, cps, name, version, author, email, description);
            Runtime.Notify("Proxy contract upgraded");
            return true;
        }

        private static object[] DeserializeArgs(byte[] buffer)
        {
            var offset = 0;
            var res = ReadVarBytes(buffer, offset);
            var assetAddress = res[0];

            res = ReadVarBytes(buffer, (int)res[1]);
            var toAddress = res[0];

            res = ReadUint255(buffer, (int)res[1]);
            var amount = res[0];

            return new object[] { assetAddress, toAddress, amount };
        }

        private static object[] ReadUint255(byte[] buffer, int offset)
        {
            if (offset + 32 > buffer.Length)
            {
                Runtime.Notify("Length is not long enough");
                return new object[] { 0, -1 };
            }
            return new object[] { buffer.Range(offset, 32).ToBigInteger(), offset + 32 };
        }

        // return [BigInteger: value, int: offset]
        private static object[] ReadVarInt(byte[] buffer, int offset)
        {
            var res = ReadBytes(buffer, offset, 1); // read the first byte
            var fb = (byte[])res[0];
            if (fb.Length != 1)
            {
                Runtime.Notify("Wrong length");
                return new object[] { 0, -1 };
            }
            var newOffset = (int)res[1];
            if (fb == new byte[] { 0xFD })
            {
                return new object[] { buffer.Range(newOffset, 2).ToBigInteger(), newOffset + 2 };
            }
            else if (fb == new byte[] { 0xFE })
            {
                return new object[] { buffer.Range(newOffset, 4).ToBigInteger(), newOffset + 4 };
            }
            else if (fb == new byte[] { 0xFF })
            {
                return new object[] { buffer.Range(newOffset, 8).ToBigInteger(), newOffset + 8 };
            }
            else
            {
                return new object[] { fb.ToBigInteger(), newOffset };
            }
        }

        // return [byte[], new offset]
        private static object[] ReadVarBytes(byte[] buffer, int offset)
        {
            var res = ReadVarInt(buffer, offset);
            var count = (int)res[0];
            var newOffset = (int)res[1];
            return ReadBytes(buffer, newOffset, count);
        }

        // return [byte[], new offset]
        private static object[] ReadBytes(byte[] buffer, int offset, int count)
        {
            if (offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
            return new object[] { buffer.Range(offset, count), offset + count };
        }

        private static byte[] SerializeArgs(byte[] assetHash, byte[] address, BigInteger amount)
        {
            var buffer = new byte[] { };
            buffer = WriteVarBytes(assetHash, buffer);
            buffer = WriteVarBytes(address, buffer);
            buffer = WriteUint255(amount, buffer);
            return buffer;
        }

        private static byte[] WriteUint255(BigInteger value, byte[] source)
        {
            if (value < 0)
            {
                Runtime.Notify("Value out of range of uint255");
                return source;
            }
            var v = PadRight(value.ToByteArray(), 32);
            return source.Concat(v); // no need to concat length, fix 32 bytes
        }

        private static byte[] WriteVarInt(BigInteger value, byte[] Source)
        {
            if (value < 0)
            {
                return Source;
            }
            else if (value < 0xFD)
            {
                return Source.Concat(value.ToByteArray());
            }
            else if (value <= 0xFFFF) // 0xff, need to pad 1 0x00
            {
                byte[] length = new byte[] { 0xFD };
                var v = PadRight(value.ToByteArray(), 2);
                return Source.Concat(length).Concat(v);
            }
            else if (value <= 0XFFFFFFFF) //0xffffff, need to pad 1 0x00 
            {
                byte[] length = new byte[] { 0xFE };
                var v = PadRight(value.ToByteArray(), 4);
                return Source.Concat(length).Concat(v);
            }
            else //0x ff ff ff ff ff, need to pad 3 0x00
            {
                byte[] length = new byte[] { 0xFF };
                var v = PadRight(value.ToByteArray(), 8);
                return Source.Concat(length).Concat(v);
            }
        }

        private static byte[] WriteVarBytes(byte[] value, byte[] Source)
        {
            return WriteVarInt(value.Length, Source).Concat(value); 
        }

        // add padding zeros on the right
        private static byte[] PadRight(byte[] value, int length)
        {
            var l = value.Length;
            if (l > length)
                return value.Range(0, length);
            for (int i = 0; i < length - l; i++)
            {
                value = value.Concat(new byte[] { 0x00 });
            }
            return value;
        }
    }
}
