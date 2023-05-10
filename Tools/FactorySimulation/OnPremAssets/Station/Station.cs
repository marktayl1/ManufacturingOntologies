
namespace Mes.Simulation
{
    using Opc.Ua;
    using System.Runtime.Serialization;

    [DataContract(Name = "StationConfig", Namespace = Namespaces.OpcUaConfig)]
    public class Station
    {
        [DataMember(Order = 1, IsRequired = true)]
        public NodeId StatusNode { get; set; }

        [DataMember(Order = 2, IsRequired = true)]
        public NodeId RootMethodNode { get; set; }

        [DataMember(Order = 3, IsRequired = true)]
        public NodeId ResetMethodNode { get; set; }

        [DataMember(Order = 4, IsRequired = true)]
        public NodeId ExecuteMethodNode { get; set; }

        [DataMember(Order = 5, IsRequired = true)]
        public NodeId StationProductTypeNode { get; set; }

        [DataMember(Order = 6, IsRequired = true)]
        public NodeId ProductSerialNumberNode { get; set; }

        [DataMember(Order = 7, IsRequired = true)]
        public NodeId NumberOfManufacturedProductsNode { get; set; }

        [DataMember(Order = 8, IsRequired = true)]
        public NodeId NumberOfDiscardedProductsNode { get; set; }

        [DataMember(Order = 9, IsRequired = true)]
        public NodeId OverallRunningTimeNode { get; set; }

        [DataMember(Order = 10, IsRequired = true)]
        public NodeId FaultyTimeNode { get; set; }

        [DataMember(Order = 11, IsRequired = true)]
        public NodeId EnergyConsumptionNode { get; set; }

        [DataMember(Order = 12, IsRequired = true)]
        public NodeId PressureNode { get; set; }

        [DataMember(Order = 13, IsRequired = true)]
        public NodeId IdealCycleTimeNode { get; set; }

        [DataMember(Order = 14, IsRequired = true)]
        public NodeId StationTypeNode { get; set; }

        [DataMember(Order = 15, IsRequired = true)]
        public NodeId ActualCycleTimeNode { get; set; }

        public Station() { }
    }
}
