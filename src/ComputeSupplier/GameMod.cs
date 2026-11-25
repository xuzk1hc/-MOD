using System;
using UnityEngine;

namespace ComputeSupplier
{
    public sealed class GameMod : IMod
    {
        private const string ModId = "compute_supplier";
        private const string ScenarioId = ModId + ".operator";
        private const string TerminalId = ModId + ".console";
        private const string NetworkId = ModId + ".network_console";
        private const string StateKey = "economy_v1";
        private const string SpriteKey = "console";

        private ModLog log;
        private GameUi ui;
        private bool invalidSave;

        internal EconomyState State { get; private set; }
        internal int Credits => PlayerStore.Instance?.playerCash ?? 0;

        public void Init(ModManifest manifest)
        {
            log = new ModLog(manifest);
            ui = new GameUi(this);
        }

        public void OnEnable()
        {
            ModHook.OnModItemDirectoryInit += RegisterItems;
            ModHook.OnGameLoadedLate += LoadRun;
            ModHook.OnNewGameStarted += OnNewRun;
            ModHook.OnGoingSleepEarly += SettleNight;
            ModHook.OnLeavingStoreEarly += CloseUi;

            ModHelper.AddStartingScenario(ModId, ScenarioId,
                title: () => "算力供应商",
                description: () => "经营二手机架。为客户跑任务，也可以从批发商采购算力交付。",
                startWith: new Func<string>[] { () => "算力终端", () => "双节点机架与两张二手显卡", () => "算力批发渠道" },
                onStart: () => StartBusiness(rackDeployed: true),
                iconKey: ModId + ":" + SpriteKey);

            ModHelper.SetNetworkTabLabel(ModId, () => "算力供应商");
            ModHelper.AddNetworkUpgrade(ModId, NetworkId, 0, "Service",
                () => "算力终端",
                () => "开通算力业务并领取终端。已经入行时，可从这里打开业务面板。",
                OpenFromNetwork,
                iconKey: ModId + ":" + SpriteKey,
                cooldownDays: 1,
                tab: ModId);
        }

        public void OnDisable()
        {
            ModHook.OnModItemDirectoryInit -= RegisterItems;
            ModHook.OnGameLoadedLate -= LoadRun;
            ModHook.OnNewGameStarted -= OnNewRun;
            ModHook.OnGoingSleepEarly -= SettleNight;
            ModHook.OnLeavingStoreEarly -= CloseUi;
            ModHelper.RemoveStartingScenarios(ModId);
            ModHelper.RemoveNetworkUpgrades(ModId);
            CloseUi();
            State = null;
        }

        private void RegisterItems(ModItemDirectory directory)
        {
            directory.Add(TerminalId, () =>
            {
                GameItem terminal = ItemDirectory.CreateEmptyItem(TerminalId);
                terminal.name = "算力终端";
                terminal.shortDescription = "查看客户订单、控制服务器机架并查询算力结算。";
                terminal.SetSpriteAndShapeFromMod(ModId, SpriteKey);
                PixelWindow window = MachineHelper.CreateMachineryWindow(true,
                    MachineHelper.Panel("业务终端", new MachineryButton("打开", ui.Open), "control"));
                terminal.SetContentWindow(window);
                return terminal;
            });
        }

        private void OnNewRun(string scenarioId)
        {
            // The specialization's onStart runs first. Read only this run's state, including those starting items.
            LoadRun();
        }

        private void LoadRun()
        {
            CloseUi();
            State = null;
            invalidSave = false;
            string saved = ModHelper.GetModData(ModId, StateKey, null);
            if (saved == null)
                return;

            try
            {
                EconomyState state = JsonUtility.FromJson<EconomyState>(saved);
                if (!Economy.IsValidState(state))
                    throw new FormatException("算力业务数据格式无效。");

                if (Economy.SkipToDay(state, Today))
                    Commit(state);
                else
                    State = state;
            }
            catch (Exception exception)
            {
                invalidSave = true;
                log.Error("算力业务存档无法读取，原始数据已保留。" + exception.Message);
            }
        }

        private void StartBusiness(bool rackDeployed)
        {
            // onStart can run before OnNewGameStarted, with this IMod instance reused across stores.
            if (ModHelper.HasModData(ModId, StateKey))
                return;

            invalidSave = false;
            Commit(Economy.Create(Today, rackDeployed));
            ModHelper.MoveItemToInventory(ModHelper.SpawnItem(TerminalId));
        }

        private void OpenFromNetwork()
        {
            if (invalidSave)
            {
                log.Warning("当前算力业务存档无法读取，请查看算力供应商的错误日志。");
                return;
            }

            if (State == null)
                StartBusiness(rackDeployed: false);
            ModHelper.CloseWildNetwork();
            ui.Open();
        }

        private static int Today => Math.Max(1, StoreStation.GetDayCounter());

        private void SettleNight()
        {
            CloseUi();
            if (State == null)
                return;

            int day = Today;
            if (day < State.Day)
                return;

            EconomyState next = CopyState();
            Economy.SkipToDay(next, day);
            if (Economy.TrySettleNight(next, day, out NightLedger ledger, out string reason))
                Commit(next, ledger.CreditsDelta);
            else
                log.Warning("算力夜间结算未完成。" + reason);
        }

        internal string TryAcceptOrder(string offerId, Fulfillment fulfillment)
        {
            if (State == null)
                return "没有已开通的算力业务。";

            EconomyState next = CopyState();
            if (!Economy.TryAccept(next, offerId, fulfillment, Credits, out OrderQuote quote, out string reason))
                return reason;

            Commit(next, -quote.SupplierCost);
            return quote.SupplierCost > 0
                ? $"接单成功。已支付 {quote.SupplierCost} 信用点采购算力。"
                : "接单成功。今夜交付。";
        }

        internal string TrySetPowerMode(PowerMode mode)
        {
            if (State == null)
                return "没有已开通的算力业务。";

            EconomyState next = CopyState();
            if (!Economy.TrySetMode(next, mode, out string reason))
                return reason;

            Commit(next);
            return "功率档位已切换。";
        }

        internal string TryDeployRack()
        {
            if (State == null)
                return "没有已开通的算力业务。";

            EconomyState next = CopyState();
            if (!Economy.TryDeployRack(next, Credits, out string reason))
                return reason;

            Commit(next, -Economy.RackPrice);
            return "双节点机架已部署。";
        }

        internal string TryInstallOptical()
        {
            if (State == null)
                return "没有已开通的算力业务。";

            EconomyState next = CopyState();
            if (!Economy.TryInstallOptical(next, Credits, out string reason))
                return reason;

            Commit(next, -Economy.OpticalPrice);
            return "两台服务器之间的光互联已接通。";
        }

        internal string TryReplaceGpu(int slot)
        {
            if (State == null)
                return "没有已开通的算力业务。";

            EconomyState next = CopyState();
            if (!Economy.TryReplaceGpu(next, slot, Credits, out string reason))
                return reason;

            int cost = Economy.GetGpuReplacementCost(State, slot);
            Commit(next, -cost);
            return $"节点 {slot + 1} 的显卡已换新，折抵后支付 {cost} 信用点。";
        }

        private EconomyState CopyState() => JsonUtility.FromJson<EconomyState>(JsonUtility.ToJson(State));

        private void Commit(EconomyState next, int creditChange = 0)
        {
            string json = JsonUtility.ToJson(next);
            if (creditChange != 0)
                ModHelper.AddCredit(creditChange, false);
            ModHelper.SetModData(ModId, StateKey, json);
            State = next;
        }

        private void CloseUi() => ui.Close();

    }
}
