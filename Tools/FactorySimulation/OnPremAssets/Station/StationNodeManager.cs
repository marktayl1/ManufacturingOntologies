
namespace Station.Simulation
{
    using Newtonsoft.Json;
    using Opc.Ua;
    using Opc.Ua.Export;
    using Opc.Ua.Server;
    using Org.BouncyCastle.Crypto;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;
    using static System.Net.Mime.MediaTypeNames;

    public enum StationStatus : int
    {
        Ready = 0,
        WorkInProgress = 1,
        Done = 2,
        Discarded = 3,
        Fault = 4
    }

    public class StationNodeManager : CustomNodeManager2
    {
        private ulong m_overallRunningTime = 0;
        private ulong m_faultyTime = 0;
        private ulong m_idealCycleTime = Program.CycleTime * 1000;        // [ms]
        private ulong m_actualCycleTime = Program.CycleTime * 500;        // [ms]
        private ulong m_idealCycleTimeDefault = Program.CycleTime * 1000; // [ms]
        private ulong m_idealCycleTimeMinimum = Program.CycleTime * 500;  // [ms]
        private const int c_failureCycleTime = 5000;                      // [ms]
        private DateTime m_cycleStartTime;

        private const ulong c_pressureDefault = 2500;          // [mbar]
        private double m_pressure = c_pressureDefault;         // [mbar]

        private StationStatus m_status = StationStatus.Ready;
        private double m_energyConsumption = 0;
        private ulong m_productSerialNumber = 1;
        private ulong m_numberOfManufacturedProducts = 0;
        private ulong m_numberOfDiscardedProducts = 0;

        private Stopwatch m_faultClock = new Stopwatch();
        private Timer m_simulationTimer = null;

        private Random m_random = new Random();

        private ushort m_namespaceIndex;
        private long m_lastUsedId;

        private NodeId m_NumberOfManufacturedProductsID;
        private NodeId m_NumberOfDiscardedProductsID;
        private NodeId m_ProductSerialNumberID;
        private NodeId m_ActualCycleTimeID;
        private NodeId m_FaultyTimeID;
        private NodeId m_IdealCycleTimeID;
        private NodeId m_OverallRunningTimeID;
        private NodeId m_EnergyConsumptionID;
        private NodeId m_PressureID;
        private NodeId m_StatusID;
        private StringBuilder csv;
        private int csv_rowcount;

        public StationNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration)
        {
            SystemContext.NodeIdFactory = this;

            List<string> namespaceUris = new List<string>();
            namespaceUris.Add("http://opcfoundation.org/UA/Station/");
            NamespaceUris = namespaceUris;

            csv = new StringBuilder();
            csv_rowcount = 0;

            m_namespaceIndex = Server.NamespaceUris.GetIndexOrAppend(namespaceUris[0]);
            m_lastUsedId = 0;

            m_faultClock.Reset();
        }

        public override NodeId New(ISystemContext context, NodeState node)
        {
            return new NodeId(Utils.IncrementIdentifier(ref m_lastUsedId), m_namespaceIndex);
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                IList<IReference> references = null;
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                ImportNodeset2Xml(externalReferences, "Station.NodeSet2.xml");

                AddReverseReferences(externalReferences);
            }
        }

        // Nodeset2 files can be edited using e.g. the SIEMENS OPC UA Modeling Editor (SiOME)
        // see https://support.industry.siemens.com/cs/document/109755133/siemens-opc-ua-modeling-editor-(siome)-for-implementing-opc-ua-companion-specification
        private void ImportNodeset2Xml(IDictionary<NodeId, IList<IReference>> externalReferences, string resourcepath)
        {
            NodeStateCollection predefinedNodes = new NodeStateCollection();

            Stream stream = new FileStream(resourcepath, FileMode.Open);
            UANodeSet nodeSet = UANodeSet.Read(stream);

            foreach (string namespaceUri in nodeSet.NamespaceUris)
            {
                SystemContext.NamespaceUris.GetIndexOrAppend(namespaceUri);
            }

            nodeSet.Import(SystemContext, predefinedNodes);

            for (int i = 0; i < predefinedNodes.Count; i++)
            {
                AddPredefinedNode(SystemContext, predefinedNodes[i]);
            }
        }

        protected override NodeState AddBehaviourToPredefinedNode(ISystemContext context, NodeState predefinedNode)
        {
            // add behaviour to our methods
            MethodState methodState = predefinedNode as MethodState;
            if ((methodState != null) && (methodState.ModellingRuleId == null))
            {
                if (methodState.DisplayName == "Execute")
                {
                    methodState.OnCallMethod = new GenericMethodCalledEventHandler(Execute);

                    // define the method's input argument (the serial number)
                    methodState.InputArguments = new PropertyState<Argument[]>(methodState)
                    {
                        NodeId = new NodeId(methodState.BrowseName.Name + "InArgs", NamespaceIndex),
                        BrowseName = BrowseNames.InputArguments
                    };
                    methodState.InputArguments.DisplayName = methodState.InputArguments.BrowseName.Name;
                    methodState.InputArguments.TypeDefinitionId = VariableTypeIds.PropertyType;
                    methodState.InputArguments.ReferenceTypeId = ReferenceTypeIds.HasProperty;
                    methodState.InputArguments.DataType = DataTypeIds.Argument;
                    methodState.InputArguments.ValueRank = ValueRanks.OneDimension;

                    methodState.InputArguments.Value = new Argument[]
                    {
                        new Argument { Name = "SerialNumber", Description = "Serial number of the product to make.",  DataType = DataTypeIds.UInt64, ValueRank = ValueRanks.Scalar }
                    };

                    return predefinedNode;
                }

                if (methodState.DisplayName == "Reset")
                {
                    methodState.OnCallMethod = new GenericMethodCalledEventHandler(Reset);
                    return predefinedNode;
                }

                if (methodState.DisplayName == "OpenPressureReleaseValve")
                {
                    methodState.OnCallMethod = new GenericMethodCalledEventHandler(OpenPressureReleaseValve);
                    return predefinedNode;
                }
            }

            // also capture the nodeIDs of our instance variables (i.e. NOT the model!)
            BaseDataVariableState variableState = predefinedNode as BaseDataVariableState;
            if ((variableState != null) && (variableState.ModellingRuleId == null))
            {
                if (variableState.DisplayName == "NumberOfManufacturedProducts")
                {
                    m_NumberOfManufacturedProductsID = variableState.NodeId;
                }

                if (variableState.DisplayName == "NumberOfDiscardedProducts")
                {
                    m_NumberOfDiscardedProductsID = variableState.NodeId;
                }

                if (variableState.DisplayName == "ProductSerialNumber")
                {
                    m_ProductSerialNumberID = variableState.NodeId;
                }

                if (variableState.DisplayName == "ActualCycleTime")
                {
                    m_ActualCycleTimeID = variableState.NodeId;
                }

                if (variableState.DisplayName == "EnergyConsumption")
                {
                    m_EnergyConsumptionID = variableState.NodeId;
                }

                if (variableState.DisplayName == "FaultyTime")
                {
                    m_FaultyTimeID = variableState.NodeId;
                }

                if (variableState.DisplayName == "IdealCycleTime")
                {
                    m_IdealCycleTimeID = variableState.NodeId;
                }

                if (variableState.DisplayName == "OverallRunningTime")
                {
                    m_OverallRunningTimeID = variableState.NodeId;
                }

                if (variableState.DisplayName == "Pressure")
                {
                    m_PressureID = variableState.NodeId;
                }

                if (variableState.DisplayName == "Status")
                {
                    m_StatusID = variableState.NodeId;
                }
            }

            return predefinedNode;
        }

        private ServiceResult Execute(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            if (m_status == StationStatus.Fault)
            {
                ServiceResult result = new ServiceResult(new Exception("Machine is in fault state, call reset first!"));
                return result;
            }

            m_productSerialNumber = (ulong)inputArguments[0];

            m_cycleStartTime = DateTime.UtcNow;

            m_status = StationStatus.WorkInProgress;

            ulong idealCycleTime = m_idealCycleTime;
            if (idealCycleTime < m_idealCycleTimeMinimum)
            {
                m_idealCycleTime = idealCycleTime = m_idealCycleTimeMinimum;
            }

            int cycleTime = (int)(idealCycleTime + Convert.ToUInt32(Math.Abs((double)idealCycleTime * NormalDistribution(m_random, 0.0, 0.1))));

            bool stationFailure = (NormalDistribution(m_random, 0.0, 1.0) > 3.0);
            if (stationFailure)
            {
                // the simulated cycle will take longer when the station fails
                cycleTime = c_failureCycleTime + Convert.ToInt32(Math.Abs((double)c_failureCycleTime * NormalDistribution(m_random, 0.0, 1.0)));
            }

            m_simulationTimer = new Timer(SimulationFinished, stationFailure, cycleTime, Timeout.Infinite);

            UpdateNodeValues();

            return ServiceResult.Good;
        }

        private void SimulationFinished(object state)
        {
            CalculateSimulationResult((bool)state);

            UpdateNodeValues();

            m_simulationTimer.Dispose();
        }


        private ServiceResult Reset(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            m_faultClock.Stop();
            m_status = StationStatus.Ready;

            UpdateNodeValues();

            return ServiceResult.Good;
        }

        private ServiceResult OpenPressureReleaseValve(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            m_pressure = c_pressureDefault;

            UpdateNodeValues();

            return ServiceResult.Good;
        }

        private void UpdateNodeValues()
        {

            NodeState node = Find(m_NumberOfManufacturedProductsID);
            BaseDataVariableState variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_numberOfManufacturedProducts;
                variableState.Timestamp = DateTime.UtcNow;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_NumberOfDiscardedProductsID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_numberOfDiscardedProducts;
                variableState.Timestamp = DateTime.UtcNow;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_ProductSerialNumberID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_productSerialNumber;
                variableState.Timestamp = DateTime.UtcNow;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_ActualCycleTimeID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_actualCycleTime;
                variableState.Timestamp = DateTime.UtcNow;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_EnergyConsumptionID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_energyConsumption;
                variableState.Timestamp = DateTime.UtcNow;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_FaultyTimeID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_faultyTime;
                variableState.Timestamp = DateTime.UtcNow;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_IdealCycleTimeID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_idealCycleTime;
                variableState.Timestamp = DateTime.UtcNow;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_OverallRunningTimeID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_overallRunningTime;
                variableState.Timestamp = DateTime.UtcNow;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_PressureID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_pressure;
                variableState.Timestamp = DateTime.UtcNow;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_StatusID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_status;
                variableState.Timestamp = DateTime.UtcNow;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            if (!m_faultClock.IsRunning)
            {
                m_faultyTime = (ulong)m_faultClock.ElapsedMilliseconds;
                if (m_faultClock.ElapsedMilliseconds != 0)
                {
                    m_faultClock.Reset();
                }
            }

            //in your loop
            var newLine = string.Format("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20}",
                m_numberOfManufacturedProducts,DateTime.UtcNow,m_numberOfDiscardedProducts, DateTime.UtcNow, m_productSerialNumber,DateTime.UtcNow,
                m_energyConsumption,DateTime.UtcNow, m_faultyTime,DateTime.UtcNow, m_idealCycleTime,DateTime.UtcNow,
                m_overallRunningTime, DateTime.UtcNow, m_pressure, DateTime.UtcNow, m_status,DateTime.UtcNow, m_faultyTime,DateTime.UtcNow);

            csv.AppendLine(newLine);
            csv_rowcount++;

            if (csv_rowcount == 100)
            {
                // _ = Program._storage.StoreFileAsync("csv-data", Encoding.ASCII.GetBytes(csv.ToString()));
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string filePath = Path.Combine(baseDir, string.Format("SimulationData_{0}.csv",DateTime.UtcNow));
                using (StreamWriter writer = new(filePath))
                {
                    writer.Write(csv);
                }
                csv.Clear();
                csv_rowcount = 0;
            }
        }

        //private void UpdateNodeValues_Json()
        //{
        //    // Reading SimulationData Json file
        //    //string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        //    //string filePath = Path.Combine(baseDir, "SimulationData.json");
        //    //dynamic jsonData = JsonConvert.DeserializeObject<dynamic>(System.IO.File.ReadAllText(filePath));
        //    //Program._storage.StoreFileAsync("SimulationData", jsonData.Encoding.UTF8.GetBytes(System.IO.File.ReadAllText(filePath)));


        //    //NodeState node = Find(m_NumberOfManufacturedProductsID);
        //    //BaseDataVariableState variableState = node as BaseDataVariableState;
        //    //if (variableState != null)
        //    //{
        //    //    variableState.Value = jsonData["NumberOfManufacturedProducts"];
        //    //    variableState.Timestamp = DateTime.UtcNow;
        //    //    variableState.ClearChangeMasks(SystemContext, false);
        //    //}

        //    //node = Find(m_NumberOfDiscardedProductsID);
        //    //variableState = node as BaseDataVariableState;
        //    //if (variableState != null)
        //    //{
        //    //    variableState.Value = jsonData["NumberOfDiscardedProducts"];
        //    //    variableState.Timestamp = DateTime.UtcNow;
        //    //    variableState.ClearChangeMasks(SystemContext, false);
        //    //}

        //    //node = Find(m_ProductSerialNumberID);
        //    //variableState = node as BaseDataVariableState;
        //    //if (variableState != null)
        //    //{
        //    //    variableState.Value = jsonData["ProductSerialNumber"];
        //    //    variableState.Timestamp = DateTime.UtcNow;
        //    //    variableState.ClearChangeMasks(SystemContext, false);
        //    //}

        //    //node = Find(m_ActualCycleTimeID);
        //    //variableState = node as BaseDataVariableState;
        //    //if (variableState != null)
        //    //{
        //    //    variableState.Value = jsonData["ActualCycleTime"]; //m_actualCycleTime;
        //    //    variableState.Timestamp = DateTime.UtcNow;
        //    //    variableState.ClearChangeMasks(SystemContext, false);
        //    //}

        //    //node = Find(m_EnergyConsumptionID);
        //    //variableState = node as BaseDataVariableState;
        //    //if (variableState != null)
        //    //{
        //    //    variableState.Value = jsonData["EnergyConsumption"]; //m_energyConsumption;
        //    //    variableState.Timestamp = DateTime.UtcNow;
        //    //    variableState.ClearChangeMasks(SystemContext, false);
        //    //}

        //    //node = Find(m_FaultyTimeID);
        //    //variableState = node as BaseDataVariableState;
        //    //if (variableState != null)
        //    //{
        //    //    variableState.Value = jsonData["FaultyTime"]; //m_faultyTime;
        //    //    variableState.Timestamp = DateTime.UtcNow;
        //    //    variableState.ClearChangeMasks(SystemContext, false);
        //    //}

        //    //node = Find(m_IdealCycleTimeID);
        //    //variableState = node as BaseDataVariableState;
        //    //if (variableState != null)
        //    //{
        //    //    variableState.Value = jsonData["IdealCycleTime"]; //m_idealCycleTime;
        //    //    variableState.Timestamp = DateTime.UtcNow;
        //    //    variableState.ClearChangeMasks(SystemContext, false);
        //    //}

        //    //node = Find(m_OverallRunningTimeID);
        //    //variableState = node as BaseDataVariableState;
        //    //if (variableState != null)
        //    //{
        //    //    variableState.Value = jsonData["OverallRunningTime"]; //m_overallRunningTime;
        //    //    variableState.Timestamp = DateTime.UtcNow;
        //    //    variableState.ClearChangeMasks(SystemContext, false);
        //    //}

        //    //node = Find(m_PressureID);
        //    //variableState = node as BaseDataVariableState;
        //    //if (variableState != null)
        //    //{
        //    //    variableState.Value = jsonData["Pressure"]; //m_pressure;
        //    //    variableState.Timestamp = DateTime.UtcNow;
        //    //    variableState.ClearChangeMasks(SystemContext, false);
        //    //}

        //    //node = Find(m_StatusID);
        //    //variableState = node as BaseDataVariableState;
        //    //if (variableState != null)
        //    //{
        //    //    variableState.Value = jsonData["Status"]; //m_status;
        //    //    variableState.Timestamp = DateTime.UtcNow;
        //    //    variableState.ClearChangeMasks(SystemContext, false);
        //    //}

        //    //if (!m_faultClock.IsRunning)
        //    //{
        //    //    m_faultyTime = (ulong)m_faultClock.ElapsedMilliseconds;
        //    //    if (m_faultClock.ElapsedMilliseconds != 0)
        //    //    {
        //    //        m_faultClock.Reset();
        //    //    }
        //    //}
        //}

        public virtual void CalculateSimulationResult(bool stationFailure)
        {
            bool productDiscarded = (NormalDistribution(m_random, 0.0, 1.0) > 2.0);

            if (stationFailure)
            {
                m_numberOfDiscardedProducts++;
                m_status = StationStatus.Fault;
                m_faultClock.Start();
            }
            else if (productDiscarded)
            {
                m_status = StationStatus.Discarded;
                m_numberOfDiscardedProducts++;
            }
            else
            {
                m_status = StationStatus.Done;
                m_numberOfManufacturedProducts++;
            }

            m_actualCycleTime = (ulong)(DateTime.UtcNow - m_cycleStartTime).TotalMilliseconds;

            double idealCycleTime = m_idealCycleTime;

            // The power consumption of the station increases exponentially if the ideal cycle time is reduced below the default ideal cycle time 
            double cycleTimeModifier = (1 / Math.E) * (1 / Math.Exp(-(double)m_idealCycleTimeDefault / idealCycleTime));
            double powerConsumption = Program.PowerConsumption * cycleTimeModifier;

            // assume the station consumes only power during the active cycle
            // energy consumption [kWh] = (PowerConsumption [kW] * actualCycleTime [s]) / 3600
            m_energyConsumption = (powerConsumption * ((double)m_actualCycleTime / 1000.0)) / 3600.0;

            // slowly increase pressure until c_pressureHigh is reached
            m_pressure += Math.Abs(NormalDistribution(m_random, (cycleTimeModifier - 1.0) * 10.0, 10.0));

            // keep pressure within our bounds
            if (m_pressure < c_pressureDefault)
            {
                m_pressure = c_pressureDefault;
            }
        }

        private double NormalDistribution(Random rand, double mean, double stdDev)
        {
            // it's possible to convert a generic normal distribution function f(x) to a standard
            // normal distribution (a normal distribution with mean=0 and stdDev=1) with the
            // following formula:
            //
            //  z = (x - mean) / stdDev
            //
            // then with z value you can retrieve the probability value P(X>x) from the standard
            // normal distribution table 

            // these are uniform(0,1) random doubles
            double u1 = rand.NextDouble();
            double u2 = rand.NextDouble();

            // random normal(0,1)
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

            // random normal(mean,stdDev^2)
            return mean + stdDev * randStdNormal;
        }
    }
}
