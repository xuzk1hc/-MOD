using System;

namespace ComputeSupplier
{
    internal sealed class GameUi
    {
        private const string WindowId = "compute_supplier.terminal";

        private enum View
        {
            Orders,
            Rack,
            Ledger
        }

        private readonly GameMod mod;
        private View currentView;
        private Fulfillment route;
        private string feedback = string.Empty;

        internal GameUi(GameMod mod)
        {
            this.mod = mod;
        }

        internal void Open()
        {
            Render();
        }

        internal void Close()
        {
            currentView = View.Orders;
            route = Fulfillment.Local;
            feedback = string.Empty;

            var manager = CustomUIManager.Instance;
            if (manager != null)
                manager.CloseWindow(WindowId);
        }

        private void Render()
        {
            var state = mod.State;
            if (state == null || CustomUIManager.Instance == null)
                return;

            int credits = mod.Credits;
            var window = CustomUIManager.Window(WindowId, "算力业务终端", CustomUIManager.LAYER_PAPER)
                .UseMachineryStyle()
                .SetSize(640, 440)
                .SetDraggable(true)
                .AddLabel($"第 {state.Day} 天    可用信用点 {credits:N0}")
                .BeginRow()
                    .AddButton(ViewLabel(View.Orders, "订单"), () => ShowView(View.Orders))
                    .AddButton(ViewLabel(View.Rack, "机架"), () => ShowView(View.Rack))
                    .AddButton(ViewLabel(View.Ledger, "账本"), () => ShowView(View.Ledger))
                    .AddFlexibleSpace()
                    .AddButton("关闭", Close)
                .End()
                .AddSeparator();

            if (!string.IsNullOrEmpty(feedback))
                window.AddLabel(feedback);

            window.BeginScrollFlexible(240);
            switch (currentView)
            {
                case View.Orders:
                    RenderOrders(window, state, credits);
                    break;
                case View.Rack:
                    RenderRack(window, state, credits);
                    break;
                case View.Ledger:
                    RenderLedger(window, state);
                    break;
            }
            window.End().Show();
        }

        private void RenderOrders(CustomUIBuilder window, EconomyState state, int credits)
        {
            if (state.RackDeployed)
            {
                var capacity = Economy.GetUsableCapacity(state);
                window.AddLabel($"自营可用算力    批处理 {capacity.BatchUnits}    多卡协同 {capacity.CoupledUnits}");
            }
            else
            {
                window.AddLabel("机架尚未部署    当前履约方式为全外采");
            }

            window.AddLabel($"批发剩余额度 {Economy.GetSupplierRemaining(state)} / {Economy.GetSupplierDailyQuota(state)}")
                .AddLabel($"批发单价    批处理 {Economy.GetSupplierUnitPrice(state, Workload.Batch)}    多卡协同 {Economy.GetSupplierUnitPrice(state, Workload.Coupled)} 信用点／单位");

            if (state.RackDeployed)
            {
                window.BeginRow()
                    .AddButton(RouteLabel(Fulfillment.Local), () => SelectRoute(Fulfillment.Local))
                    .AddButton(RouteLabel(Fulfillment.Mixed), () => SelectRoute(Fulfillment.Mixed))
                    .AddButton(RouteLabel(Fulfillment.Outsource), () => SelectRoute(Fulfillment.Outsource))
                    .End();
            }

            var selectedRoute = state.RackDeployed ? route : Fulfillment.Outsource;
            var offers = Economy.GetOffers(state);
            if (offers.Count == 0)
                window.AddLabel("今天没有其他订单。");

            foreach (var offer in offers)
            {
                var quote = Economy.GetQuote(state, offer.Id, selectedRoute);
                window.AddSeparator()
                    .AddHeader(WorkloadLabel(offer.Workload))
                    .AddLabel($"需求 {offer.Units} 单位    今夜交付    报酬 {offer.Revenue:N0} 信用点");

                if (!quote.IsAvailable)
                {
                    window.AddWarning(quote.Reason);
                    continue;
                }

                window.AddLabel($"自营 {quote.LocalUnits}    外采 {quote.SupplierUnits}    电费 {quote.PowerCost:N0}    采购费 {quote.SupplierCost:N0}")
                    .AddLabel($"预计现金利润 {Signed(quote.NetProfit)}    折旧 {quote.DepreciationCost:N0}    扣折旧后 {Signed(quote.NetProfit - quote.DepreciationCost)}")
                    .AddLabel($"显卡 1／2 预计磨损 {quote.Gpu0Wear:0.##}／{quote.Gpu1Wear:0.##} 个百分点");

                if (quote.SupplierCost > credits)
                {
                    window.AddWarning("信用点不足，外采费用在接单时支付。");
                    continue;
                }

                window.AddButton("接单", () => RunOperation(() => mod.TryAcceptOrder(offer.Id, selectedRoute)));
            }

            if (state.AcceptedOrders.Count == 0)
                return;

            window.AddSeparator().AddHeader("待交付");
            foreach (var order in state.AcceptedOrders)
            {
                window.AddLabel($"{WorkloadLabel(order.Offer.Workload)}    {FulfillmentLabel(order.Fulfillment)}    {order.Offer.Units} 单位    报酬 {order.Offer.Revenue:N0}");
            }
        }

        private void RenderRack(CustomUIBuilder window, EconomyState state, int credits)
        {
            window.AddHeader("服务器机架");
            if (!state.RackDeployed)
            {
                window.AddLabel($"部署一台带两张显卡的机架需 {Economy.RackPrice:N0} 信用点。");
                if (credits < Economy.RackPrice)
                    window.AddWarning("当前余额不足。");
                else
                    window.AddButton($"部署机架 · {Economy.RackPrice:N0}", () => RunOperation(mod.TryDeployRack));
                return;
            }

            var capacity = Economy.GetUsableCapacity(state);
            bool locked = Economy.IsHardwareLocked(state);

            window.AddLabel($"当前档位    {PowerLabel(state.Mode)}")
                .AddLabel($"峰值功率 {capacity.PeakPowerWatts:N0} W    今夜电价 {Economy.GetPowerPrice(state):N0} 信用点／kWh")
                .AddLabel($"可用算力    批处理 {capacity.BatchUnits}    多卡协同 {capacity.CoupledUnits}");

            if (locked)
            {
                window.AddWarning("已接的自营订单占用了机架。交付结算后可调整硬件和档位。");
            }
            else
            {
                var nextMode = state.Mode == PowerMode.Eco ? PowerMode.Performance : PowerMode.Eco;
                window.AddButton($"切换到{PowerLabel(nextMode)}", () => RunOperation(() => mod.TrySetPowerMode(nextMode)))
                    .WithTooltip("性能档提高单位时间内的算力，同时增加耗电和磨损。");
            }

            window.AddSeparator().AddHeader("显卡");
            RenderGpu(window, state, credits, locked, 0, capacity.Gpu0Units);
            RenderGpu(window, state, credits, locked, 1, capacity.Gpu1Units);

            window.AddSeparator().AddHeader("光互联");
            if (state.HasOptical)
            {
                window.AddLabel("已安装    提高多卡协同订单的吞吐。");
            }
            else
            {
                window.AddLabel("未安装    光互联可提高多卡协同订单的吞吐。");
                if (!locked && credits >= Economy.OpticalPrice)
                    window.AddButton($"安装光互联 · {Economy.OpticalPrice:N0}", () => RunOperation(mod.TryInstallOptical));
                else if (!locked)
                    window.AddLabel($"安装需 {Economy.OpticalPrice:N0} 信用点。");
            }
        }

        private void RenderGpu(CustomUIBuilder window, EconomyState state, int credits, bool locked, int slot, int units)
        {
            decimal health = state.Gpus[slot].Health;
            int residualValue = Economy.GetGpuResidualValue(state, slot);
            window.AddLabel($"显卡 {slot + 1}    健康 {health:0.##}%    可用算力 {units}    残值 {residualValue:N0} 信用点");

            if (locked || health >= 100)
                return;

            int cost = Economy.GetGpuReplacementCost(state, slot);
            if (cost > credits)
            {
                window.AddLabel($"显卡 {slot + 1} 折抵后需 {cost:N0} 信用点，当前余额不足。");
                return;
            }

            window.AddButton($"更换显卡 {slot + 1} · 折抵后 {cost:N0}", () => RunOperation(() => mod.TryReplaceGpu(slot)));
        }

        private static void RenderLedger(CustomUIBuilder window, EconomyState state)
        {
            var ledger = state.LastLedger;
            if (ledger == null)
            {
                window.AddLabel("第一晚结算后可查看账本。");
                return;
            }

            window.AddHeader($"第 {ledger.Day} 天夜间结算")
                .AddLabel($"交付 {ledger.OrderCount} 单    算力 {ledger.DeliveredUnits} 单位")
                .AddLabel($"订单收入    {ledger.Revenue:N0} 信用点")
                .AddLabel($"采购费用    {ledger.SupplierCost:N0} 信用点（接单时支付）")
                .AddLabel($"电费    {ledger.PowerCost:N0} 信用点")
                .AddLabel($"订单现金利润    {Signed(ledger.Profit)} 信用点")
                .AddLabel($"折旧    {ledger.DepreciationCost:N0} 信用点    扣折旧后利润 {Signed(ledger.Profit - ledger.DepreciationCost)}")
                .AddLabel($"本次结算入账    {Signed(ledger.CreditsDelta)} 信用点")
                .AddLabel($"显卡 1／2 磨损    {ledger.Gpu0Wear:0.##}／{ledger.Gpu1Wear:0.##} 个百分点");
        }

        private void ShowView(View view)
        {
            currentView = view;
            feedback = string.Empty;
            Render();
        }

        private void SelectRoute(Fulfillment fulfillment)
        {
            route = fulfillment;
            feedback = string.Empty;
            Render();
        }

        private void RunOperation(Func<string> operation)
        {
            feedback = operation();
            Render();
        }

        private string ViewLabel(View view, string label) => currentView == view ? $"【{label}】" : label;

        private string RouteLabel(Fulfillment fulfillment)
        {
            string label = FulfillmentLabel(fulfillment);
            return route == fulfillment ? $"【{label}】" : label;
        }

        private static string FulfillmentLabel(Fulfillment fulfillment)
        {
            switch (fulfillment)
            {
                case Fulfillment.Local:
                    return "全自营";
                case Fulfillment.Mixed:
                    return "外采补缺";
                case Fulfillment.Outsource:
                    return "全外采";
                default:
                    throw new ArgumentOutOfRangeException(nameof(fulfillment));
            }
        }

        private static string WorkloadLabel(Workload workload) => workload == Workload.Batch ? "批处理订单" : "多卡协同订单";

        private static string PowerLabel(PowerMode mode) => mode == PowerMode.Eco ? "节能档" : "性能档";

        private static string Signed(int credits) => credits.ToString("+#,0;-#,0;0");
    }
}
