﻿using System;
using System.Linq;
using FNPlugin.Constants;
using FNPlugin.Extensions;
using FNPlugin.Resources;
using KSP.Localization;
using UnityEngine;

namespace FNPlugin.Refinery.Activity
{
    [KSPModule("ISRU Methane Pyrolysis")]
    class MethanePyrolysis : RefineryActivity
    {
        public MethanePyrolysis()
        {
            ActivityName = "Methane Pyrolysis";
            Formula = "CH<size=7>4</size> => H<size=7>2</size> + C";
            PowerRequirements = PluginSettings.Config.BaseELCPowerConsumption;
            EnergyPerTon = PluginSettings.Config.ElectrolysisEnergyPerTon;
        }

        private double _monoxideDensity;
        private double _methaneDensity;
        private double _hydrogenDensity;
        private double _oxygenDensity;

        private double _methaneConsumptionRate;
        private double _oxygenConsumptionRate;

        private double _hydrogenProductionRate;
        private double _monoxideProductionRate;

        private string _monoxideResourceName;
        private string _methaneResourceName;
        private string _hydrogenResourceName;
        private string _oxygenResourceName;

        private double _maxCapacityMonoxideMass;
        private double _maxCapacityHydrogenMass;
        private double _maxCapacityMethaneMass;
        private double _maxCapacityOxygenMass;

        private double _availableMethaneMass;
        private double _availableOxygenMass;
        private double _spareRoomHydrogenMass;
        private double _spareRoomMonoxideMass;

        private const double MonoxideMassByFraction = 1 - 18.01528 / (18.01528 + 28.010); // taken from reverse water gas shift
        private const double HydrogenMassByFraction = (8 * 1.008) / (44.01 + (8 * 1.008));
        private const double OxygenMassByFraction = 32.0 / 52.0;
        private const double MethaneMassByFraction = 20.0 / 52.0;

        private double _combinedConsumptionRate;

        public override string Status => string.Copy(_status);
        public override RefineryType RefineryType => RefineryType.Heating;

        public override bool HasActivityRequirements()
        {
            if (_monoxideResourceName == null || _hydrogenResourceName == null || _methaneResourceName == null || _oxygenResourceName == null)
                return false;

            return _part.GetConnectedResources(_methaneResourceName).Any(rs => rs.amount > 0) &
                _part.GetConnectedResources(_oxygenResourceName).Any(rs => rs.amount > 0);
        }

        public override void Initialize(Part localPart, InterstellarRefineryController controller)
        {
            base.Initialize(localPart, controller);

            _monoxideResourceName = ResourceSettings.Config.CarbonMonoxideGas;
            _hydrogenResourceName = ResourceSettings.Config.HydrogenGas;
            _methaneResourceName = ResourceSettings.Config.MethaneGas;
            _oxygenResourceName = ResourceSettings.Config.OxygenGas;

            _monoxideDensity = PartResourceLibrary.Instance.GetDefinition(_monoxideResourceName).density;
            _hydrogenDensity = PartResourceLibrary.Instance.GetDefinition(_hydrogenResourceName).density;
            _methaneDensity = PartResourceLibrary.Instance.GetDefinition(_methaneResourceName).density;
            _oxygenDensity = PartResourceLibrary.Instance.GetDefinition(_oxygenResourceName).density;
        }

        public override void UpdateFrame(double rateMultiplier, double powerFraction, double productionModifier, bool allowOverflow, double fixedDeltaTime, bool isStartup = false)
        {
            _current_power = PowerRequirements * rateMultiplier;
            _current_rate = CurrentPower / EnergyPerTon;

            // determine how much resource we have
            var partsThatContainMonoxide = _part.GetConnectedResources(_monoxideResourceName).ToList();
            var partsThatContainHydrogen = _part.GetConnectedResources(_hydrogenResourceName).ToList();
            var partsThatContainMethane = _part.GetConnectedResources(_methaneResourceName).ToList();
            var partsThatContainOxygen = _part.GetConnectedResources(_oxygenResourceName).ToList();

            // determine the maximum amount of a resource the vessel can hold (ie. tank capacities combined)
            _maxCapacityMonoxideMass = partsThatContainMonoxide.Sum(p => p.maxAmount) * _monoxideDensity;
            _maxCapacityHydrogenMass = partsThatContainHydrogen.Sum(p => p.maxAmount) * _hydrogenDensity;
            _maxCapacityMethaneMass = partsThatContainMethane.Sum(p => p.maxAmount) * _methaneDensity;
            _maxCapacityOxygenMass = partsThatContainOxygen.Sum(p => p.maxAmount) * _oxygenDensity;

            // determine the amount of resources needed for pyrolysis that the vessel actually holds
            _availableMethaneMass = partsThatContainMethane.Sum(r => r.amount) * _methaneDensity;
            _availableOxygenMass = partsThatContainOxygen.Sum(r => r.amount) * _oxygenDensity;

            // determine how much spare room there is in the vessel's resource tanks (for the resources this is going to produce)
            _spareRoomMonoxideMass = partsThatContainMonoxide.Sum(r => r.maxAmount - r.amount) * _monoxideDensity;
            _spareRoomHydrogenMass = partsThatContainHydrogen.Sum(r => r.maxAmount - r.amount) * _hydrogenDensity;

            // this should determine how much resources this process can consume
            var fixedMaxMethaneConsumptionRate = _current_rate * MethaneMassByFraction * fixedDeltaTime;
            var methaneConsumptionRatio = fixedMaxMethaneConsumptionRate > 0
                ? Math.Min(fixedMaxMethaneConsumptionRate, _availableMethaneMass) / fixedMaxMethaneConsumptionRate
                : 0;

            var fixedMaxOxygenConsumptionRate = _current_rate * OxygenMassByFraction * fixedDeltaTime;
            var oxygenConsumptionRatio = fixedMaxOxygenConsumptionRate > 0 ? Math.Min(fixedMaxOxygenConsumptionRate, _availableOxygenMass) / fixedMaxOxygenConsumptionRate : 0;

            var fixedConsumptionRate = _current_rate * fixedDeltaTime * Math.Min(methaneConsumptionRatio, oxygenConsumptionRatio);

            // begin the pyrolysis process
            if (fixedConsumptionRate > 0 && (_spareRoomHydrogenMass > 0 || _spareRoomMonoxideMass > 0))
            {

                var fixedMaxMonoxideRate = fixedConsumptionRate * MonoxideMassByFraction;
                var fixedMaxHydrogenRate = fixedConsumptionRate * HydrogenMassByFraction;

                var fixedMaxPossibleMonoxideRate = allowOverflow ? fixedMaxMonoxideRate : Math.Min(_spareRoomMonoxideMass, fixedMaxMonoxideRate);
                var fixedMaxPossibleHydrogenRate = allowOverflow ? fixedMaxHydrogenRate : Math.Min(_spareRoomHydrogenMass, fixedMaxHydrogenRate);

                var consumptionStorageRatio = Math.Min(fixedMaxPossibleMonoxideRate / fixedMaxMonoxideRate, fixedMaxPossibleHydrogenRate / fixedMaxHydrogenRate);

                // this consumes the resources
                _oxygenConsumptionRate = _part.RequestResource(_oxygenResourceName, OxygenMassByFraction * consumptionStorageRatio * fixedConsumptionRate / _oxygenDensity, ResourceFlowMode.ALL_VESSEL) / fixedDeltaTime * _oxygenDensity;
                _methaneConsumptionRate = _part.RequestResource(_methaneResourceName, MethaneMassByFraction * consumptionStorageRatio * fixedConsumptionRate / _methaneDensity, ResourceFlowMode.ALL_VESSEL) / fixedDeltaTime * _methaneDensity;
                _combinedConsumptionRate = _oxygenConsumptionRate + _methaneConsumptionRate;

                // this produces the products
                var monoxideRateTemp = _combinedConsumptionRate * MonoxideMassByFraction;
                var waterRateTemp = _combinedConsumptionRate * HydrogenMassByFraction;

                _monoxideProductionRate = -_part.RequestResource(_monoxideResourceName, -monoxideRateTemp * fixedDeltaTime / _monoxideDensity) / fixedDeltaTime * _monoxideDensity;
                _hydrogenProductionRate = -_part.RequestResource(_hydrogenResourceName, -waterRateTemp * fixedDeltaTime / _hydrogenDensity) / fixedDeltaTime * _hydrogenDensity;
            }
            else
            {
                _methaneConsumptionRate = 0;
                _oxygenConsumptionRate = 0;
                _hydrogenProductionRate = 0;
            }
            UpdateStatusMessage();
        }

        public override void UpdateGUI()
        {
            base.UpdateGUI();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_Power"), _bold_label, GUILayout.Width(labelWidth));//"Power"
            GUILayout.Label(PluginHelper.GetFormattedPowerString(CurrentPower) + "/" + PluginHelper.GetFormattedPowerString(PowerRequirements), _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_OveralConsumption"), _bold_label, GUILayout.Width(labelWidth));//"Overal Consumption"
            GUILayout.Label(((_combinedConsumptionRate * GameConstants.SECONDS_IN_HOUR).ToString("0.0000")) + " mT/hour", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_OxygenAvailable"), _bold_label, GUILayout.Width(labelWidth));//"Oxygen Available"
            GUILayout.Label(_availableOxygenMass.ToString("0.0000") + " mT / " + _maxCapacityOxygenMass.ToString("0.0000") + " mT", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_OxygenConsumptionRate"), _bold_label, GUILayout.Width(labelWidth));//"Oxygen Consumption Rate"
            GUILayout.Label((_oxygenConsumptionRate * GameConstants.SECONDS_IN_HOUR).ToString("0.000") + " mT/hour", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_MethaneAvailable"), _bold_label, GUILayout.Width(labelWidth));//"Methane Available"
            GUILayout.Label(_availableMethaneMass.ToString("0.0000") + " mT / " + _maxCapacityMethaneMass.ToString("0.0000") + " mT", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_ConsumptionRate"), _bold_label, GUILayout.Width(labelWidth));//"Methane Consumption Rate"
            GUILayout.Label((_methaneConsumptionRate * GameConstants.SECONDS_IN_HOUR).ToString("0.000") + " mT/hour", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_HydrogenStorage"), _bold_label, GUILayout.Width(labelWidth));//"Hydrogen Storage"
            GUILayout.Label(_spareRoomHydrogenMass.ToString("0.0000") + " mT / " + _maxCapacityHydrogenMass.ToString("0.0000") + " mT", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_HydrogenProductionRate"), _bold_label, GUILayout.Width(labelWidth));//"Hydrogen Production Rate"
            GUILayout.Label((_hydrogenProductionRate * GameConstants.SECONDS_IN_HOUR).ToString("0.000") + " mT/hour", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_CarbonMonoxideStorage"), _bold_label, GUILayout.Width(labelWidth));//"Carbon Monoxide Storage"
            GUILayout.Label(_spareRoomMonoxideMass.ToString("0.0000") + " mT / " + _maxCapacityMonoxideMass.ToString("0.0000") + " mT", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_CarbonMonoxideProductionRate"), _bold_label, GUILayout.Width(labelWidth));//"Carbon Monoxide Production Rate"
            GUILayout.Label((_monoxideProductionRate * GameConstants.SECONDS_IN_HOUR).ToString("0.000") + " mT/hour", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();
        }

        private void UpdateStatusMessage()
        {
            if (_hydrogenProductionRate > 0)
                _status = Localizer.Format("#LOC_KSPIE_MethanePyrolysis_Statumsg1");//"Methane Pyrolysis Ongoing"
            else if (CurrentPower <= 0.01*PowerRequirements)
                _status = Localizer.Format("#LOC_KSPIE_MethanePyrolysis_Statumsg2");//"Insufficient Power"
            else
                _status = Localizer.Format("#LOC_KSPIE_MethanePyrolysis_Statumsg3");//"Insufficient Storage"
        }

        public override void PrintMissingResources()
        {
            if (!_part.GetConnectedResources(_methaneResourceName).Any(rs => rs.amount > 0))
                ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_PostMsg") +" " + _methaneResourceName, 3.0f, ScreenMessageStyle.UPPER_CENTER);//Missing
            if (!_part.GetConnectedResources(_oxygenResourceName).Any(rs => rs.amount > 0))
                ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_PostMsg") +" " + _oxygenResourceName, 3.0f, ScreenMessageStyle.UPPER_CENTER);//Missing
        }
    }
}
