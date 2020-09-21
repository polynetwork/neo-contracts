using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System;
using System.ComponentModel;
using System.Numerics;

// [assembly: ContractTitle("optional contract title")]
// [assembly: ContractDescription("optional contract description")]
// [assembly: ContractVersion("optional contract version")]
// [assembly: ContractAuthor("optional contract author")]
// [assembly: ContractEmail("optional contract email")]
[assembly: Features(ContractPropertyState.HasStorage | ContractPropertyState.HasDynamicInvoke | ContractPropertyState.Payable)]

namespace MockNep5
{
    public class ETHX : SmartContract
    {
        [DisplayName("transfer")]
        public static event Action<byte[], byte[], BigInteger> Transferred;

        public static event Action<byte[], byte[]> TransferOwnershipEvent;

        private static readonly byte[] Owner = "".ToScriptHash(); //Owner Address
        private static readonly byte[] ZERO_ADDRESS = "0000000000000000000000000000000000000000".HexToBytes();

        //private const ulong factor = 1000000000000000000; //decided by Decimals()
        private static readonly BigInteger total_amount = new BigInteger("000064a7b3b6e00d".HexToBytes()); // total token amount, 1*10^18

        public static object Main(string method, object[] args)
        {
            if (Runtime.Trigger == TriggerType.Verification)
            {
                return Runtime.CheckWitness(GetOwner());
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                var callscript = ExecutionEngine.CallingScriptHash;

                // Contract deployment
                if (method == "deploy")
                    return Deploy();
                if (method == "isDeployed")
                    return IsDeployed();

                // NEP5 standard methods
                if (method == "balanceOf") return BalanceOf((byte[])args[0]);

                if (method == "decimals") return Decimals();

                if (method == "name") return Name();

                if (method == "symbol") return Symbol();

                if (method == "totalSupply") return TotalSupply();

                if (method == "transfer") return Transfer((byte[])args[0], (byte[])args[1], (BigInteger)args[2], callscript);

                // Owner management
                if (method == "transferOwnership")
                    return TransferOwnership((byte[])args[0]);
                if (method == "getOwner")
                    return GetOwner();

                // Contract management
                if (method == "supportedStandards")
                    return SupportedStandards();
                if (method == "pause")
                    return Pause();
                if (method == "unpause")
                    return Unpause();
                if (method == "isPaused")
                    return IsPaused();
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

        #region -----Contract deployment-----
        [DisplayName("deploy")]
        public static bool Deploy()
        {
            if (!Runtime.CheckWitness(Owner))
            {
                Runtime.Notify("Only owner can deploy this contract.");
                return false;
            }
            if (IsDeployed())
            {
                Runtime.Notify("Already deployed");
                return false;
            }

            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            contract.Put("totalSupply", total_amount);
            contract.Put("owner", Owner);

            StorageMap asset = Storage.CurrentContext.CreateMap(nameof(asset));
            asset.Put(Owner, total_amount);
            Transferred(null, Owner, total_amount);
            return true;
        }

        [DisplayName("isDeployed")]
        public static bool IsDeployed()
        {
            // if totalSupply has value, means deployed
            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            byte[] total_supply = contract.Get("totalSupply");
            return total_supply.Length != 0;
        }
        #endregion

        #region -----NEP5 standard methods-----
        [DisplayName("balanceOf")]
        public static BigInteger BalanceOf(byte[] account)
        {
            if (!IsAddress(account))
            {
                Runtime.Notify("The parameter account SHOULD be a legal address.");
                return 0;
            }
            StorageMap asset = Storage.CurrentContext.CreateMap(nameof(asset));
            return asset.Get(account).AsBigInteger();
        }

        [DisplayName("decimals")]
        public static byte Decimals() => 9;

        [DisplayName("name")]
        public static string Name() => "pONT NEP5"; //name of the token

        [DisplayName("symbol")]
        public static string Symbol() => "pONT"; //symbol of the token

        [DisplayName("totalSupply")]
        public static BigInteger TotalSupply()
        {
            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            return contract.Get("totalSupply").AsBigInteger();
        }
#if DEBUG
        [DisplayName("transfer")] //Only for ABI file
        public static bool Transfer(byte[] from, byte[] to, BigInteger amount) => true;
#endif
        //Methods of actual execution
        private static bool Transfer(byte[] from, byte[] to, BigInteger amount, byte[] callscript)
        {
            if (IsPaused())
            {
                Runtime.Notify("ONTX contract is paused.");
                return false;
            }
            //Check parameters
            if (!IsAddress(from) || !IsAddress(to))
            {
                Runtime.Notify("The parameters from and to SHOULD be legal addresses.");
                return false;
            }
            if (amount <= 0)
            {
                Runtime.Notify("The parameter amount MUST be greater than 0.");
                return false;
            }
            if (!IsPayable(to))
            {
                Runtime.Notify("The to account is not payable.");
                return false;
            }
            if (!Runtime.CheckWitness(from) && from.AsBigInteger() != callscript.AsBigInteger())
            {
                // either the tx is signed by "from" or is called by "from"
                Runtime.Notify("Not authorized by the from account");
                return false;
            }

            StorageMap asset = Storage.CurrentContext.CreateMap(nameof(asset));
            var fromAmount = asset.Get(from).AsBigInteger();
            if (fromAmount < amount)
            {
                Runtime.Notify("Insufficient funds");
                return false;
            }
            if (from == to)
                return true;

            //Reduce payer balances
            if (fromAmount == amount)
                asset.Delete(from);
            else
                asset.Put(from, fromAmount - amount);

            //Increase the payee balance
            var toAmount = asset.Get(to).AsBigInteger();
            asset.Put(to, toAmount + amount);

            Transferred(from, to, amount);
            return true;
        }
        #endregion

        #region -----Owner Management-----
        [DisplayName("transferOwnership")]
        public static bool TransferOwnership(byte[] newOwner)
        {
            // transfer contract ownership from current owner to a new owner
            if (!Runtime.CheckWitness(GetOwner()))
            {
                Runtime.Notify("Only allowed to be called by owner.");
                return false;
            }
            if (!IsAddress(newOwner))
            {
                Runtime.Notify("The parameter newOwner SHOULD be a legal address.");
                return false;
            }

            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            var preowner = contract.Get("owner");
            contract.Put("owner", newOwner);
            TransferOwnershipEvent(preowner, newOwner);
            return true;
        }

        [DisplayName("getOwner")]
        public static byte[] GetOwner()
        {
            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            var owner = contract.Get("owner");
            return owner;
        }
        #endregion

        #region -----Contract management-----
        [DisplayName("supportedStandards")]
        public static string[] SupportedStandards() => new string[] { "NEP-5", "NEP-7", "NEP-10" };

        [DisplayName("pause")]
        public static bool Pause()
        {
            // Set the smart contract to paused state, the token can not be transfered, approved.
            // Only can invoke some get interface, like getOwner.
            if (!Runtime.CheckWitness(GetOwner()))
            {
                Runtime.Notify("Only allowed to be called by owner.");
                return false;
            }
            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            contract.Put("paused", 1);
            return true;
        }

        [DisplayName("unpause")]
        public static bool Unpause()
        {
            if (!Runtime.CheckWitness(GetOwner()))
            {
                Runtime.Notify("Only allowed to be called by owner.");
                return false;
            }
            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            contract.Put("paused", 0);
            return true;
        }

        [DisplayName("isPaused")]
        public static bool IsPaused()
        {
            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            return contract.Get("paused").AsBigInteger() != 0;
        }

        [DisplayName("upgrade")]
        public static bool Upgrade(byte[] newScript, byte[] paramList, byte returnType, ContractPropertyState cps,
            string name, string version, string author, string email, string description)
        {
            if (!Runtime.CheckWitness(GetOwner()))
            {
                Runtime.Notify("Only allowed to be called by owner.");
                return false;
            }
            var contract = Contract.Migrate(newScript, paramList, returnType, cps, name, version, author, email, description);
            Runtime.Notify("Proxy contract upgraded");
            return true;
        }
        #endregion

        #region -----Helper methods-----
        private static bool IsAddress(byte[] address)
        {
            return address.Length == 20 && address.AsBigInteger() != ZERO_ADDRESS.AsBigInteger();
        }

        private static bool IsPayable(byte[] to)
        {
            var c = Blockchain.GetContract(to);
            return c == null || c.IsPayable;
        }
        #endregion
    }
}
