using System;
using System.Collections.Generic;
using System.Linq;

namespace ComputeSupplier
{
    public enum PowerMode { Eco, Performance }
    public enum Workload { Batch, Coupled }
    public enum Fulfillment { Local, Outsource, Mixed }

    [Serializable]
    public sealed class EconomyState
    {
        public int LastSettledDay;
        public int Day => LastSettledDay + 1;
        public bool RackDeployed;
        public GpuState[] Gpus;
        public PowerMode Mode;
        public bool HasOptical;
        public List<OrderQuote> AcceptedOrders = new List<OrderQuote>();
        public bool HasLedger;
        public NightLedger SavedLedger;
        public NightLedger LastLedger
        {
            get => HasLedger ? SavedLedger : null;
            set
            {
                HasLedger = value != null;
                SavedLedger = value;
            }
        }
    }

    [Serializable]
    public sealed class GpuState
    {
        public int HealthHundredths;
        public decimal Health
        {
            get => HealthHundredths / 100m;
            set => HealthHundredths = (int)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
        }
    }

    public sealed class CapacitySummary
    {
        public int BatchUnits { get; set; }
        public int CoupledUnits { get; set; }
        public int Gpu0Units { get; set; }
        public int Gpu1Units { get; set; }
        public int PeakPowerWatts { get; set; }
    }

    [Serializable]
    public sealed class OrderOffer
    {
        public string Id;
        public Workload Workload;
        public int Units;
        public int Revenue;
    }

    [Serializable]
    public sealed class OrderQuote
    {
        public OrderOffer Offer;
        public Fulfillment Fulfillment;
        public bool IsAvailable;
        public string Reason;
        public int LocalUnits;
        public int SupplierUnits;
        public int SupplierCost;
        public int PowerCost;
        public int Depreciation;
        public int DepreciationCost => Depreciation;
        public int NetProfit => Offer == null ? 0 : Offer.Revenue - SupplierCost - PowerCost;
        public int Gpu0RawUnits;
        public int Gpu1RawUnits;
        public int Gpu0WearHundredths;
        public int Gpu1WearHundredths;
        public decimal Gpu0Wear => Gpu0WearHundredths / 100m;
        public decimal Gpu1Wear => Gpu1WearHundredths / 100m;
    }

    [Serializable]
    public sealed class NightLedger
    {
        public int Day;
        public int Revenue;
        public int SupplierCost;
        public int PowerCost;
        public int Depreciation;
        public int DepreciationCost => Depreciation;
        public int Profit => CreditsDelta - SupplierCost;
        public int CreditsDelta => Revenue - PowerCost;
        public int OrderCount;
        public int DeliveredUnits;
        public int Gpu0WearHundredths;
        public int Gpu1WearHundredths;
        public decimal Gpu0Wear => Gpu0WearHundredths / 100m;
        public decimal Gpu1Wear => Gpu1WearHundredths / 100m;
    }

    public static class Economy
    {
        public const int RackPrice = 260;
        public const int ReplacementGpuPrice = 145;
        public const int OpticalPrice = 220;

        private const int DailyOfferCount = 3;
        private const int OperatingHours = 6;
        private const int MaximumGpuResidualValue = 72;
        private const int StarterGpu0HealthHundredths = 8400;
        private const int StarterGpu1HealthHundredths = 7000;
        private const decimal MinimumGpuHealth = 15m;

        public static EconomyState Create(int day, bool rackDeployed = true)
        {
            return new EconomyState
            {
                LastSettledDay = day - 1,
                RackDeployed = rackDeployed,
                // A broker save retains the two starter slots so rack deployment needs no save-shape change.
                Gpus = new[]
                {
                    new GpuState { HealthHundredths = StarterGpu0HealthHundredths },
                    new GpuState { HealthHundredths = StarterGpu1HealthHundredths }
                }
            };
        }

        public static IReadOnlyList<OrderOffer> GetOffers(EconomyState state)
        {
            int day = state.Day;
            int smallUnits = 24 + MarketSample(day, 1, 8);
            int bulkUnits = 60 + MarketSample(day, 2, 13);
            int coupledUnits = 48 + MarketSample(day, 3, 15);
            var offers = new[]
            {
                MakeOffer(day, "small", Workload.Batch, smallUnits, smallUnits * 5),
                MakeOffer(day, "bulk", Workload.Batch, bulkUnits, bulkUnits * 4 + 10),
                MakeOffer(day, "coupled", Workload.Coupled, coupledUnits, coupledUnits * 7 + 12)
            };
            return offers.Where(offer => state.AcceptedOrders.All(order => order.Offer.Id != offer.Id)).ToArray();
        }

        public static int GetSupplierDailyQuota(EconomyState state) => 72 + MarketSample(state.Day, 4, 20);

        public static int GetSupplierRemaining(EconomyState state) =>
            GetSupplierDailyQuota(state) - state.AcceptedOrders.Sum(order => order.SupplierUnits);

        public static int GetSupplierUnitPrice(EconomyState state, Workload workload) =>
            workload == Workload.Coupled ? 6 + MarketSample(state.Day, 6, 2) : 3 + MarketSample(state.Day, 5, 3);

        public static int GetPowerPrice(EconomyState state) => 14 + MarketSample(state.Day, 7, 11);

        public static int GetGpuResidualValue(EconomyState state, int slot) =>
            state.RackDeployed ? ResidualValue(state.Gpus[slot].HealthHundredths) : 0;

        public static int GetGpuReplacementCost(EconomyState state, int slot) =>
            ReplacementGpuPrice - GetGpuResidualValue(state, slot);

        public static bool IsHardwareLocked(EconomyState state) => state.AcceptedOrders.Any(order => order.LocalUnits > 0);

        public static CapacitySummary GetUsableCapacity(EconomyState state)
        {
            int capacity0 = GetGpuCapacity(state, 0);
            int capacity1 = GetGpuCapacity(state, 1);
            int free0 = capacity0 - state.AcceptedOrders.Sum(order => order.Gpu0RawUnits);
            int free1 = capacity1 - state.AcceptedOrders.Sum(order => order.Gpu1RawUnits);
            int usableCards = (capacity0 > 0 ? 1 : 0) + (capacity1 > 0 ? 1 : 0);
            return new CapacitySummary
            {
                BatchUnits = free0 + free1,
                CoupledUnits = Math.Min(free0, free1) * 2 * InterconnectPercent(state) / 100,
                Gpu0Units = free0,
                Gpu1Units = free1,
                PeakPowerWatts = usableCards == 0 ? 0 : RackWatts(state) + usableCards * GpuWatts(state)
            };
        }

        public static OrderQuote GetQuote(EconomyState state, string offerId, Fulfillment fulfillment)
        {
            OrderOffer offer = GetOffers(state).FirstOrDefault(candidate => candidate.Id == offerId);
            if (offer == null)
                return Unavailable(offer, fulfillment, "这张订单已接取或已过期。");

            var capacity = GetUsableCapacity(state);
            int localUnits;
            switch (fulfillment)
            {
                case Fulfillment.Local:
                    int usable = offer.Workload == Workload.Coupled ? capacity.CoupledUnits : capacity.BatchUnits;
                    if (usable < offer.Units)
                        return Unavailable(offer, fulfillment, "剩余本地算力不足。");
                    localUnits = offer.Units;
                    break;
                case Fulfillment.Outsource:
                    localUnits = 0;
                    break;
                case Fulfillment.Mixed:
                    if (offer.Workload == Workload.Coupled)
                        return Unavailable(offer, fulfillment, "协同任务须在同一个集群内完成。");
                    localUnits = Math.Min(capacity.BatchUnits, offer.Units);
                    if (localUnits == 0 || localUnits == offer.Units)
                        return Unavailable(offer, fulfillment, "只有本地可交付一部分时才能补采。");
                    break;
                default:
                    return Unavailable(offer, fulfillment, "请选择有效的交付方式。");
            }

            int supplierUnits = offer.Units - localUnits;
            if (supplierUnits > GetSupplierRemaining(state))
                return Unavailable(offer, fulfillment, "批发商今日剩余额度不足。");

            var (raw0, raw1) = AllocateGpuUnits(state, capacity, offer.Workload, localUnits);
            int reserved0 = state.AcceptedOrders.Sum(order => order.Gpu0RawUnits);
            int reserved1 = state.AcceptedOrders.Sum(order => order.Gpu1RawUnits);
            int previousWear0 = GpuWearHundredths(state, 0, reserved0);
            int previousWear1 = GpuWearHundredths(state, 1, reserved1);
            int totalWear0 = GpuWearHundredths(state, 0, reserved0 + raw0);
            int totalWear1 = GpuWearHundredths(state, 1, reserved1 + raw1);
            return new OrderQuote
            {
                Offer = offer,
                Fulfillment = fulfillment,
                IsAvailable = true,
                Reason = string.Empty,
                LocalUnits = localUnits,
                SupplierUnits = supplierUnits,
                SupplierCost = supplierUnits * GetSupplierUnitPrice(state, offer.Workload),
                PowerCost = PowerCost(state, reserved0 + raw0, reserved1 + raw1) - PowerCost(state, reserved0, reserved1),
                Depreciation = GpuDepreciation(state, 0, previousWear0, totalWear0) +
                    GpuDepreciation(state, 1, previousWear1, totalWear1),
                Gpu0RawUnits = raw0,
                Gpu1RawUnits = raw1,
                Gpu0WearHundredths = totalWear0 - previousWear0,
                Gpu1WearHundredths = totalWear1 - previousWear1
            };
        }

        public static bool TryAccept(EconomyState state, string offerId, Fulfillment fulfillment, int credits,
            out OrderQuote quote, out string reason)
        {
            quote = GetQuote(state, offerId, fulfillment);
            if (!quote.IsAvailable)
                return Refuse(quote.Reason, out reason);
            if (quote.SupplierCost > Math.Max(0, credits))
                return Refuse("信用点不足，无法预付采购费。", out reason);

            state.AcceptedOrders.Add(quote);
            reason = string.Empty;
            return true;
        }

        public static bool TrySetMode(EconomyState state, PowerMode mode, out string reason)
        {
            if (!state.RackDeployed)
                return Refuse("先部署机架才能更改功率。", out reason);
            if (mode != PowerMode.Eco && mode != PowerMode.Performance)
                return Refuse("请选择有效的功率模式。", out reason);
            if (IsHardwareLocked(state))
                return Refuse("已有本地履约订单，结算后可以更改功率。", out reason);

            state.Mode = mode;
            reason = string.Empty;
            return true;
        }

        public static bool TryReplaceGpu(EconomyState state, int slot, int credits, out string reason)
        {
            if (!state.RackDeployed)
                return Refuse("先部署机架才能换卡。", out reason);
            if (slot < 0 || slot >= state.Gpus.Length)
                return Refuse("没有这个显卡槽位。", out reason);
            if (IsHardwareLocked(state))
                return Refuse("已有本地履约订单，结算后可以换卡。", out reason);
            if (state.Gpus[slot].Health >= 100m)
                return Refuse("这张卡的成色已满。", out reason);
            if (credits < GetGpuReplacementCost(state, slot))
                return Refuse("信用点不足，无法更换显卡。", out reason);

            state.Gpus[slot] = new GpuState { Health = 100m };
            reason = string.Empty;
            return true;
        }

        public static bool TryInstallOptical(EconomyState state, int credits, out string reason)
        {
            if (!state.RackDeployed)
                return Refuse("先部署机架才能安装光互联。", out reason);
            if (state.HasOptical)
                return Refuse("已安装光互联。", out reason);
            if (IsHardwareLocked(state))
                return Refuse("已有本地履约订单，结算后可以安装光互联。", out reason);
            if (credits < OpticalPrice)
                return Refuse("信用点不足，无法安装光互联。", out reason);

            state.HasOptical = true;
            reason = string.Empty;
            return true;
        }

        public static bool TryDeployRack(EconomyState state, int credits, out string reason)
        {
            if (state.RackDeployed)
                return Refuse("机架已部署。", out reason);
            if (credits < RackPrice)
                return Refuse("信用点不足，无法部署机架。", out reason);

            state.RackDeployed = true;
            reason = string.Empty;
            return true;
        }

        public static bool TrySettleNight(EconomyState state, int day, out NightLedger ledger, out string reason)
        {
            ledger = null;
            if (day <= state.LastSettledDay)
                return Refuse("这一天已结算。", out reason);
            if (day != state.Day)
                return Refuse("结算日与机架营业日不一致。", out reason);

            var orders = state.AcceptedOrders;
            ledger = new NightLedger
            {
                Day = day,
                OrderCount = orders.Count,
                DeliveredUnits = orders.Sum(order => order.Offer.Units),
                Revenue = orders.Sum(order => order.Offer.Revenue),
                SupplierCost = orders.Sum(order => order.SupplierCost),
                PowerCost = orders.Sum(order => order.PowerCost),
                Depreciation = orders.Sum(order => order.DepreciationCost),
                Gpu0WearHundredths = orders.Sum(order => order.Gpu0WearHundredths),
                Gpu1WearHundredths = orders.Sum(order => order.Gpu1WearHundredths)
            };

            state.Gpus[0].Health = Math.Max(0m, state.Gpus[0].Health - ledger.Gpu0Wear);
            state.Gpus[1].Health = Math.Max(0m, state.Gpus[1].Health - ledger.Gpu1Wear);
            state.LastLedger = ledger;
            state.LastSettledDay = day;
            orders.Clear();
            reason = string.Empty;
            return true;
        }

        public static bool SkipToDay(EconomyState state, int actualDay)
        {
            if (actualDay <= state.Day || actualDay >= int.MaxValue)
                return false;

            state.AcceptedOrders.Clear();
            state.LastSettledDay = actualDay - 1;
            return true;
        }

        public static bool IsValidState(EconomyState state)
        {
            if (state == null || state.LastSettledDay < -1 || state.LastSettledDay >= int.MaxValue - 1 ||
                state.Gpus == null || state.Gpus.Length != 2 ||
                state.Gpus.Any(gpu => gpu == null || gpu.Health < 0m || gpu.Health > 100m) ||
                (state.Mode != PowerMode.Eco && state.Mode != PowerMode.Performance) ||
                state.AcceptedOrders == null || state.AcceptedOrders.Count > DailyOfferCount)
                return false;
            if (!state.RackDeployed && (state.HasOptical || state.Mode != PowerMode.Eco ||
                state.Gpus[0].HealthHundredths != StarterGpu0HealthHundredths ||
                state.Gpus[1].HealthHundredths != StarterGpu1HealthHundredths))
                return false;

            NightLedger ledger = state.LastLedger;
            if (state.HasLedger && (ledger == null || ledger.Day > state.LastSettledDay || ledger.Day < 0 ||
                ledger.OrderCount < 0 || ledger.OrderCount > DailyOfferCount || ledger.DeliveredUnits < 0 ||
                ledger.Revenue < 0 || ledger.SupplierCost < 0 || ledger.PowerCost < 0 ||
                ledger.DepreciationCost < 0 || ledger.DepreciationCost > MaximumGpuResidualValue * 2 ||
                ledger.Gpu0Wear < 0m || ledger.Gpu1Wear < 0m))
                return false;

            var reconstructed = new EconomyState
            {
                LastSettledDay = state.LastSettledDay,
                RackDeployed = state.RackDeployed,
                Gpus = state.Gpus,
                Mode = state.Mode,
                HasOptical = state.HasOptical
            };
            foreach (OrderQuote order in state.AcceptedOrders)
            {
                if (order?.Offer == null || !order.IsAvailable)
                    return false;
                OrderQuote expected = GetQuote(reconstructed, order.Offer.Id, order.Fulfillment);
                if (!expected.IsAvailable || !SameOrder(order, expected))
                    return false;
                reconstructed.AcceptedOrders.Add(expected);
            }
            return true;
        }

        private static bool SameOrder(OrderQuote actual, OrderQuote expected) =>
            actual.Offer.Workload == expected.Offer.Workload && actual.Offer.Units == expected.Offer.Units &&
            actual.Offer.Revenue == expected.Offer.Revenue && actual.LocalUnits == expected.LocalUnits &&
            actual.SupplierUnits == expected.SupplierUnits && actual.SupplierCost == expected.SupplierCost &&
            actual.PowerCost == expected.PowerCost && actual.DepreciationCost == expected.DepreciationCost &&
            actual.Gpu0RawUnits == expected.Gpu0RawUnits &&
            actual.Gpu1RawUnits == expected.Gpu1RawUnits && actual.Gpu0Wear == expected.Gpu0Wear &&
            actual.Gpu1Wear == expected.Gpu1Wear;

        private static OrderOffer MakeOffer(int day, string name, Workload workload, int units, int revenue) =>
            new OrderOffer
            {
                Id = day.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + name,
                Workload = workload,
                Units = units,
                Revenue = revenue
            };

        private static OrderQuote Unavailable(OrderOffer offer, Fulfillment fulfillment, string reason) =>
            new OrderQuote { Offer = offer, Fulfillment = fulfillment, Reason = reason };

        private static bool Refuse(string message, out string reason)
        {
            reason = message;
            return false;
        }

        private static int MarketSample(int day, uint stream, uint range)
        {
            unchecked
            {
                uint value = (uint)day * 1759714727u + stream * 2246822519u;
                value ^= value >> 16;
                value *= 73244475u;
                value ^= value >> 16;
                return (int)(value % range);
            }
        }

        private static int InterconnectPercent(EconomyState state) => state.HasOptical ? 90 : 60;
        private static int RackWatts(EconomyState state) => state.HasOptical ? 48 : 30;
        private static int GpuWatts(EconomyState state) => state.Mode == PowerMode.Performance ? 155 : 90;

        private static int GetGpuCapacity(EconomyState state, int slot)
        {
            decimal health = state.Gpus[slot].Health;
            if (!state.RackDeployed || health <= MinimumGpuHealth)
                return 0;
            int healthyCapacity = state.Mode == PowerMode.Performance ? 55 : 42;
            return (int)(healthyCapacity * (50m + health) / 150m);
        }

        private static (int first, int second) AllocateGpuUnits(EconomyState state, CapacitySummary capacity,
            Workload workload, int localUnits)
        {
            if (localUnits == 0)
                return (0, 0);
            if (workload == Workload.Coupled)
            {
                int combinedEfficiency = InterconnectPercent(state) * 2;
                int unitsPerCard = (localUnits * 100 + combinedEfficiency - 1) / combinedEfficiency;
                return (unitsPerCard, unitsPerCard);
            }

            int firstUnits = (localUnits * capacity.Gpu0Units + capacity.BatchUnits / 2) / capacity.BatchUnits;
            return (firstUnits, localUnits - firstUnits);
        }

        private static int PowerCost(EconomyState state, int raw0, int raw1)
        {
            if (raw0 == 0 && raw1 == 0)
                return 0;
            decimal utilization = GpuUtilization(state, 0, raw0) + GpuUtilization(state, 1, raw1);
            decimal kilowattHours = OperatingHours * (RackWatts(state) + GpuWatts(state) * utilization) / 1000m;
            return (int)Math.Ceiling(kilowattHours * GetPowerPrice(state));
        }

        private static int GpuWearHundredths(EconomyState state, int slot, int rawUnits)
        {
            int fullLoadWearHundredths = state.Mode == PowerMode.Performance ? 360 : 140;
            return (int)Math.Round(GpuUtilization(state, slot, rawUnits) * fullLoadWearHundredths,
                MidpointRounding.AwayFromZero);
        }

        private static int GpuDepreciation(EconomyState state, int slot, int previousWear, int totalWear)
        {
            int health = state.Gpus[slot].HealthHundredths;
            return ResidualValue(health - previousWear) - ResidualValue(health - totalWear);
        }

        private static int ResidualValue(int healthHundredths) =>
            Math.Max(0, healthHundredths) * MaximumGpuResidualValue / 10000;

        private static decimal GpuUtilization(EconomyState state, int slot, int rawUnits) =>
            rawUnits == 0 ? 0m : rawUnits / (decimal)GetGpuCapacity(state, slot);
    }
}
