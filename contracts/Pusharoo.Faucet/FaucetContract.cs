using System;
using System.ComponentModel;
using System.Numerics;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;

namespace Pusharoo.Faucet;

[DisplayName("PusharooTestnetFaucet")]
[ContractVersion("1.1.1")]
[ContractDescription("Testnet-only GAS faucet")]
[ContractPermission("0xd2a4cff31913016155e38e474a2c06d08be276cf", "balanceOf")]
[ContractPermission("0xd2a4cff31913016155e38e474a2c06d08be276cf", "transfer")]
[ContractPermission("0xfffdc93764dbaddd97c48f252a53ea4643faa3fd", "getContract")]
[ContractPermission("0xfffdc93764dbaddd97c48f252a53ea4643faa3fd", "update")]
public class FaucetContract : SmartContract
{
    private const long GasUnit = 100_000_000;
    private const long DefaultClaimAmount = 10 * GasUnit;
    private const long DailyCap = 1_000 * GasUnit;
    private const ulong DayMilliseconds = 86_400_000;

    private static readonly byte[] AdminKey = { 0x01 };
    private static readonly byte[] RelayerKey = { 0x02 };
    private static readonly byte[] ClaimAmountKey = { 0x03 };
    private static readonly byte[] PausedKey = { 0x05 };
    private static readonly byte[] DayKey = { 0x06 };
    private static readonly byte[] SpentKey = { 0x07 };
    private static readonly byte[] ReentrancyKey = { 0x08 };

    private static readonly byte[] LastClaimPrefix = { 0x20 };

    [DisplayName("Claimed")]
    public static event Action<UInt160, BigInteger, ulong> Claimed;

    [DisplayName("Funded")]
    public static event Action<UInt160, BigInteger> Funded;

    [DisplayName("PausedChanged")]
    public static event Action<bool> PausedChanged;

    [DisplayName("ClaimAmountChanged")]
    public static event Action<BigInteger, BigInteger> ClaimAmountChanged;

    [DisplayName("RelayerChanged")]
    public static event Action<UInt160, UInt160> RelayerChanged;

    [DisplayName("Withdrawn")]
    public static event Action<UInt160, BigInteger> Withdrawn;

    public static void _deploy(object data, bool update)
    {
        // Upgrades keep the existing storage and must not reinitialize admin, relayer, or limits.
        if (update) return;

        object[] accounts = (object[])data;
        if (accounts.Length != 2) throw new Exception("Deployment data must contain administrator and relayer accounts.");

        UInt160 admin = (UInt160)accounts[0];
        UInt160 relayer = (UInt160)accounts[1];
        if (admin == UInt160.Zero || relayer == UInt160.Zero || admin == relayer)
            throw new Exception("Administrator and relayer must be distinct, non-zero accounts.");
        if (!Runtime.CheckWitness(admin)) throw new Exception("Administrator must witness deployment.");

        StorageContext context = Storage.CurrentContext;
        Storage.Put(context, AdminKey, admin);
        Storage.Put(context, RelayerKey, relayer);
        Storage.Put(context, ClaimAmountKey, DefaultClaimAmount);
        Storage.Put(context, PausedKey, BigInteger.One);
        Storage.Put(context, DayKey, (ulong)0);
        Storage.Put(context, SpentKey, (long)0);
        Storage.Put(context, ReentrancyKey, BigInteger.Zero);
    }

    [DisplayName("onNEP17Payment")]
    public static void OnNEP17Payment(UInt160 from, BigInteger amount, object data)
    {
        if (Runtime.CallingScriptHash != GAS.Hash) throw new Exception("Only native GAS can fund this faucet.");
        if (amount < 0) throw new Exception("Funding amount cannot be negative.");
        Funded(from, amount);
    }

    [DisplayName("claim")]
    public static void Claim(UInt160 recipient)
    {
        if (IsPaused()) throw new Exception("Faucet is paused.");
        if (IsReentrant()) throw new Exception("Reentrant call.");

        if (recipient == UInt160.Zero || recipient == Runtime.ExecutingScriptHash)
            throw new Exception("Recipient is invalid.");
        if (ContractManagement.GetContract(recipient) is not null)
            throw new Exception("Contract recipients are not supported.");
        ulong now = Runtime.Time;

        StorageContext context = Storage.CurrentContext;
        BigInteger claimAmount = GetClaimAmount();
        StorageMap lastClaims = new(context, LastClaimPrefix);
        object lastClaimValue = lastClaims.Get(recipient);

        if (lastClaimValue is null)
        {
            RequireRelayer();
        }
        else if (!Runtime.CheckWitness(Relayer()) && !Runtime.CheckWitness(recipient))
        {
            throw new Exception("Relayer or registered recipient witness required.");
        }

        ulong today = now / DayMilliseconds;
        BigInteger spentToday = GetSpentToday(today);
        BigInteger dailyCap = DailyCap;
        if (spentToday + claimAmount > dailyCap) throw new Exception("Faucet daily allocation is exhausted.");
        if (GAS.BalanceOf(Runtime.ExecutingScriptHash) < claimAmount)
            throw new Exception("Faucet has insufficient GAS balance.");

        if (lastClaimValue is not null)
        {
            ulong lastClaim = (ulong)(BigInteger)lastClaimValue;
            if (now < lastClaim || now - lastClaim < DayMilliseconds)
                throw new Exception("Recipient may claim once every 24 hours.");
        }

        Storage.Put(context, ReentrancyKey, BigInteger.One);
        lastClaims.Put(recipient, now);
        Storage.Put(context, DayKey, today);
        Storage.Put(context, SpentKey, spentToday + claimAmount);

        if (!GAS.Transfer(Runtime.ExecutingScriptHash, recipient, claimAmount, null))
            throw new Exception("GAS transfer failed.");

        Storage.Put(context, ReentrancyKey, BigInteger.Zero);
        Claimed(recipient, claimAmount, now);
    }

    [Safe]
    [DisplayName("getStatus")]
    public static object[] GetStatus()
    {
        ulong today = Runtime.Time / DayMilliseconds;
        BigInteger spent = GetSpentToday(today);
        BigInteger cap = DailyCap;
        BigInteger remaining = cap > spent ? cap - spent : BigInteger.Zero;
        return new object[]
        {
            GetClaimAmount(),
            cap,
            IsPaused(),
            GAS.BalanceOf(Runtime.ExecutingScriptHash),
            spent,
            remaining,
            (today + 1) * DayMilliseconds,
            Admin(),
            Relayer()
        };
    }

    [Safe]
    [DisplayName("getClaimStatus")]
    public static object[] GetClaimStatus(UInt160 recipient)
    {
        StorageMap lastClaims = new(Storage.CurrentContext, LastClaimPrefix);
        bool registered = lastClaims.Get(recipient) is not null;
        ulong now = Runtime.Time;
        ulong nextClaim = now;
        string reason = "eligible";

        if (recipient == UInt160.Zero || recipient == Runtime.ExecutingScriptHash)
            reason = "invalidRecipient";
        else if (ContractManagement.GetContract(recipient) is not null)
            reason = "contractRecipientUnsupported";
        else if (IsPaused())
            reason = "paused";
        else if (GAS.BalanceOf(Runtime.ExecutingScriptHash) < GetClaimAmount())
            reason = "insufficientBalance";
        else if (lastClaims.Get(recipient) is not null)
        {
            nextClaim = (ulong)(BigInteger)lastClaims.Get(recipient) + DayMilliseconds;
            if (now < nextClaim) reason = "cooldown";
        }

        if (reason == "eligible")
        {
            ulong today = now / DayMilliseconds;
            BigInteger spent = GetSpentToday(today);
            if (spent + GetClaimAmount() > DailyCap)
            {
                reason = "dailyCapReached";
                nextClaim = (today + 1) * DayMilliseconds;
            }
        }

        bool commonEligible = reason == "eligible";
        bool sponsoredEligible = commonEligible;
        bool directEligible = registered && commonEligible;
        string sponsoredReason = reason;
        string directReason = registered ? reason : (commonEligible ? "firstSponsoredClaim" : reason);
        return new object[]
        {
            registered,
            sponsoredEligible,
            directEligible,
            nextClaim,
            sponsoredReason,
            directReason
        };
    }

    [DisplayName("setClaimAmount")]
    public static void SetClaimAmount(BigInteger amount)
    {
        RequireAdmin();
        if (amount <= 0 || amount > DailyCap)
            throw new Exception("Claim amount must be positive and no greater than the daily cap.");

        BigInteger oldAmount = GetClaimAmount();
        if (oldAmount == amount) return;
        Storage.Put(Storage.CurrentContext, ClaimAmountKey, amount);
        ClaimAmountChanged(oldAmount, amount);
    }

    [DisplayName("setPaused")]
    public static void SetPaused(bool paused)
    {
        RequireAdmin();
        Storage.Put(Storage.CurrentContext, PausedKey, paused ? BigInteger.One : BigInteger.Zero);
        PausedChanged(paused);
    }

    [DisplayName("setRelayer")]
    public static void SetRelayer(UInt160 newRelayer)
    {
        RequireAdmin();
        if (newRelayer == UInt160.Zero || newRelayer == Admin())
            throw new Exception("Relayer must be a distinct, non-zero account.");

        UInt160 oldRelayer = Relayer();
        if (oldRelayer == newRelayer) return;
        Storage.Put(Storage.CurrentContext, RelayerKey, newRelayer);
        RelayerChanged(oldRelayer, newRelayer);
    }

    [DisplayName("update")]
    public static void Update(ByteString nefFile, string manifest)
    {
        RequireAdmin();
        if (!IsPaused()) throw new Exception("Pause the faucet before upgrading it.");
        ContractManagement.Update(nefFile, manifest, null);
    }

    [DisplayName("withdraw")]
    public static void Withdraw(UInt160 recipient, BigInteger amount)
    {
        RequireAdmin();
        if (!IsPaused()) throw new Exception("Pause the faucet before withdrawing funds.");
        if (IsReentrant()) throw new Exception("Reentrant call.");
        if (recipient == UInt160.Zero || recipient == Runtime.ExecutingScriptHash)
            throw new Exception("Withdrawal recipient is invalid.");
        if (amount <= 0 || GAS.BalanceOf(Runtime.ExecutingScriptHash) < amount)
            throw new Exception("Withdrawal amount is invalid or exceeds the balance.");

        StorageContext context = Storage.CurrentContext;
        Storage.Put(context, ReentrancyKey, BigInteger.One);
        if (!GAS.Transfer(Runtime.ExecutingScriptHash, recipient, amount, null))
            throw new Exception("GAS withdrawal failed.");
        Storage.Put(context, ReentrancyKey, BigInteger.Zero);
        Withdrawn(recipient, amount);
    }

    private static void RequireAdmin()
    {
        if (!Runtime.CheckWitness(Admin())) throw new Exception("Administrator witness required.");
    }

    private static void RequireRelayer()
    {
        if (!Runtime.CheckWitness(Relayer())) throw new Exception("Relayer witness required.");
    }

    private static UInt160 Admin() => (UInt160)Storage.Get(Storage.CurrentContext, AdminKey);
    private static UInt160 Relayer() => (UInt160)Storage.Get(Storage.CurrentContext, RelayerKey);
    private static BigInteger GetClaimAmount() => (BigInteger)Storage.Get(Storage.CurrentContext, ClaimAmountKey);
    private static bool IsPaused() => (BigInteger)Storage.Get(Storage.CurrentContext, PausedKey) != BigInteger.Zero;
    private static bool IsReentrant() => (BigInteger)Storage.Get(Storage.CurrentContext, ReentrancyKey) != BigInteger.Zero;

    private static BigInteger GetSpentToday(ulong today)
    {
        StorageContext context = Storage.CurrentContext;
        ulong storedDay = (ulong)(BigInteger)Storage.Get(context, DayKey);
        if (storedDay != today) return BigInteger.Zero;
        return (BigInteger)Storage.Get(context, SpentKey);
    }
}
