// Copyright (c) 2010-2021 Antmicro
// Copyright (c) 2021 Google LLC
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Timers
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class OpenTitan_Timer : IDoubleWordPeripheral, INumberedGPIOOutput, IKnownSize
    {
        public OpenTitan_Timer(Machine machine, long frequency = 24_000_000, int numberOfTimers = 1, int numberOfTargets = 1)
        {
            // OpenTitan rv_timer has a configurable number of timers and harts/targets.
            // It is compliant with the v1.11 RISC-V privilege specification.
            // The counters are all 64-bit and each timer has a configurable prescaler and step.
            // A multi-register array containing the enable bits for each time is at offset 0x0
            // Each timer then has its own config registers starting at offsets of 0x100 * timer_index
            
            this.numberOfTargets = numberOfTargets;
            this.numberOfTimers = numberOfTimers;
            this.numberOfIrqs = numberOfTargets * numberOfTargets;
            this.frequency = frequency;
            // Look up for IRQs by timer index and target/hart index
            irqsByTimer = new Dictionary<int, Dictionary<int, IGPIO>>();
            // Array containing all IRQs
            irqs = new Dictionary<int, IGPIO>();
            timers = new Dictionary<int, Dictionary<int, ComparingTimer>>();
            for (var targetIdx = 0; targetIdx < numberOfTargets; targetIdx++)
            {
                irqsByTimer[targetIdx] = new Dictionary<int, IGPIO>();
                timers[targetIdx] = new Dictionary<int, ComparingTimer>();
                for(var timerIdx = 0; timerIdx < numberOfTimers; timerIdx++)
                {
                    irqsByTimer[targetIdx][timerIdx] = new GPIO();
                    irqs[timerIdx * numberOfTargets + targetIdx] = irqsByTimer[targetIdx][timerIdx];
                    timers[targetIdx][timerIdx] = new ComparingTimer(machine.ClockSource, frequency, this, $"OpenTitan_Timer[{targetIdx},{timerIdx}]", limit: ulong.MaxValue,
                                                        direction: Time.Direction.Ascending, workMode: Time.WorkMode.Periodic, 
                                                        enabled: false, eventEnabled: false, compare: ulong.MaxValue);
                    var i = targetIdx;
                    var j = timerIdx;
                    timers[i][j].CompareReached += delegate
                    {
                        this.Log(LogLevel.Noisy, "Timer[{0},{1}] IRQ compare event", i, j);
                        irqsByTimer[i][j].Set(true);
                    };
                    this.Log(LogLevel.Noisy, $"Creating Timer[{targetIdx},{timerIdx}]");
                }
            }

            Connections = new ReadOnlyDictionary<int, IGPIO>(irqs);

            timerEnables = new IFlagRegisterField[numberOfTargets];
            steps = new IValueRegisterField[numberOfTargets];
            prescalers = new IValueRegisterField[numberOfTargets];
            interruptEnables = new IFlagRegisterField[numberOfTargets, numberOfTimers];
            interruptStates = new IFlagRegisterField[numberOfTargets, numberOfTimers];
            interruptTests = new IFlagRegisterField[numberOfTargets, numberOfTimers];

            var registersMap = new Dictionary<long, DoubleWordRegister>();
            AddTimerControlRegisters(registersMap, 0, numberOfTargets);
            for (int i = 0; i < numberOfTargets; i++) {
                AddTimerRegisters(registersMap, 0, i);
            }
            registers = new DoubleWordRegisterCollection(this, registersMap);
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void Reset()
        {
            registers.Reset();
            for (int i = 0; i < numberOfTargets; i++)
            {
                for (int j = 0; j < numberOfTimers; j++) 
                {
                    timers[i][j].Reset();
                    irqsByTimer[i][j].Set(false);
                }
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public String GetInnerTimer(int targetIdx, int timerIdx)
        {
            ComparingTimer timer =  timers[targetIdx][timerIdx];
            return String.Format("ComparingTimer(Enabled:{0},EventEnables:{1},Compare:0x{2:X},Value:0x{3:X},Divider:0x{4:X})",
                timer.Enabled, timer.EventEnabled, timer.Compare, timer.Value, timer.Divider);
        }

        public long Size => 0x200;
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }
        public Dictionary<int, IGPIO> irqs { get; }
        public Dictionary<int, Dictionary<int, IGPIO>> irqsByTimer { get; }
        public Dictionary<int, Dictionary<int, ComparingTimer>> timers;

        private void AddTimerControlRegisters(Dictionary<long, DoubleWordRegister> registersMap, long address, int targetCount)
        {   
            var maximumControlRegCount = (int)Math.Ceiling((targetCount) / 32.0);
            for(int timerCtrlIdx = 0; timerCtrlIdx < maximumControlRegCount; timerCtrlIdx++)
            {
                var offset = timerCtrlIdx * 4;
                int targetBitCount = (targetCount < 32) ? targetCount : 32;
                targetCount -= targetBitCount;
                var controlRegister = new DoubleWordRegister(this, 0x0);
                for (int targetBitIdx = 0; targetBitIdx < targetBitCount; targetBitIdx++)
                {
                    int timerEnablesIdx = timerCtrlIdx * 32 + targetBitIdx;
                    var flagName = String.Format("CONTROL{0}_{1}", timerCtrlIdx, timerEnablesIdx);
                    this.Log(LogLevel.Noisy, $"Add Enable bit for {timerEnablesIdx} as field {flagName}");
                    controlRegister = controlRegister.WithFlag(targetBitIdx, out timerEnables[timerEnablesIdx], name: flagName, writeCallback: (idx, val) => 
                    {
                        this.Log(LogLevel.Noisy, $"Set Enable bit for {timerEnablesIdx} to {val}");
                        for (int i = 0; i < numberOfTimers; i++) 
                        {
                            timers[timerEnablesIdx][i].Enabled = val;
                        }
                    });
                }
                this.Log(LogLevel.Noisy, "Add REG: CTRL[0x{0:X}]", address + offset);
                registersMap.Add(address + offset, controlRegister);
            }
        }

        private void AddTimerRegisters(Dictionary<long, DoubleWordRegister> registersMap, long address, int targetId)
        {
            var targetxOffset = address + 0x100 * (targetId + 1);
            var compareOffset = AddTimerConfigAndValueRegisters(registersMap, targetxOffset, targetId);
            var intrEnableOffset = AddTimerCompareRegisters(registersMap, compareOffset, targetId);
            var intrStateOffset = AddMultiRegisters(registersMap, intrEnableOffset, targetId, numberOfTimers, 
                interruptEnables, flagPrefix: "IE", regName:"INTR_ENABLE", multiRegWriteCallback: (targetIdx, timerIdx, oldValue, newValue) => 
                {
                    var timer = timers[targetIdx][timerIdx];
                    timer.EventEnabled = newValue;
                }, multiRegValueProviderCallback: (targetIdx, timerIdx) => 
                {
                    var timer = timers[targetIdx][timerIdx];
                    return timer.EventEnabled;
                });
            var intrTestOffset = AddMultiRegisters(registersMap, intrStateOffset, targetId, numberOfTimers, interruptStates, flagPrefix: "IS", regName:"INTR_STATE",
                multiRegWriteCallback: (targetIdx, timerIdx, oldValue, newValue) => 
                {
                    var gpio = irqsByTimer[targetIdx][timerIdx];
                    if (newValue) 
                    {
                        gpio.Set(false);
                    }
                }, multiRegValueProviderCallback: (targetIdx, timerIdx) => 
                {
                    var gpio = irqsByTimer[targetIdx][timerIdx];
                    return gpio.IsSet;
                });
            var _ = AddMultiRegisters(registersMap, intrTestOffset, targetId, numberOfTimers, interruptTests, flagPrefix: "T", regName:"INTR_TEST",
                multiRegWriteCallback: (targetIdx, timerIdx, oldValue, newValue) => 
                {
                    var gpio = irqsByTimer[targetIdx][timerIdx];
                    if (newValue) 
                    {
                        gpio.Set(true);
                    }
                }, multiRegValueProviderCallback: (targetIdx, timerIdx) => 
                {
                    return false;
                });
        }

        private long AddTimerConfigAndValueRegisters(Dictionary<long, DoubleWordRegister> registersMap, long address, int targetId)
        {
            long offset = 0;
            var configRegister = new DoubleWordRegister(this, 0x10000);
            var valueName = String.Format($"PRESCALE_{targetId}");
            var i = targetId;
            configRegister = configRegister.WithValueField(0, 12, out prescalers[targetId], name: valueName, writeCallback: (_, val) => 
            {
                this.Log(LogLevel.Noisy, $"Set prescaler value {val} for target {targetId}");
                // TODO: Modify timer to change frequency
                TimerUpdateDivider(targetId);
            });
            valueName = String.Format($"STEP_{targetId}");
            configRegister = configRegister.WithValueField(16, 8, out steps[targetId], name: valueName, writeCallback: (_, val) => 
            {
                this.Log(LogLevel.Noisy, $"Set the step value {val} for target {targetId}");
                // TODO: Modify timer to change frequency
            });
            this.Log(LogLevel.Noisy, "Add REG: CFG{0}[0x{1:X}]", targetId, address + offset);
            registersMap.Add(address + offset, configRegister);

            offset += 4;
            valueName = String.Format($"VALUELOW_{targetId}");
            var lowerValueRegister = new DoubleWordRegister(this, 0x0).WithValueField(0, 32, name: valueName, 
                    valueProviderCallback: _ => (uint)(timers[i][0].Value & 0xFFFF_FFFF),
                    writeCallback: (_, val) => 
                    {
                        for (int j = 0; j < numberOfTimers; j++) 
                        {
                            var timer = timers[i][j];
                            ulong currentValue = timer.Value;
                            timer.Value = currentValue & (ulong)0xFFFF_FFFF_0000_0000 | (ulong) val;
                        }
                    });
            this.Log(LogLevel.Noisy, "Add REG: TIMER_V_LOWER{0}[0x{1:X}]", targetId, address + offset);
            registersMap.Add(address + offset, lowerValueRegister);

            offset += 4;
            valueName = String.Format($"VALUEHI_{targetId}");
            var hiValueRegister = new DoubleWordRegister(this, 0x0).WithValueField(0, 32, name: valueName, 
                    valueProviderCallback: (_) => (uint)(timers[i][0].Value >> 32),
                    writeCallback: (_, val) => {
                        for (int j = 0; j < numberOfTimers; j++) 
                        {
                            var timer = timers[i][j];
                            ulong currentValue = timer.Value;
                            timer.Value = (currentValue & (ulong)0x0000_0000_FFFF_FFFF) | (((ulong)val) << 32);
                        }
                    });
            this.Log(LogLevel.Noisy, "Add REG: TIMER_V_LOWER{0}[0x{1:X}]", targetId, address + offset);
            registersMap.Add(address + offset, hiValueRegister);
            return address + offset + 4;
        }

        private long AddTimerCompareRegisters(Dictionary<long, DoubleWordRegister> registersMap, long address, int targetId)
        {
            long offset = 0;
            for(int timerIdx = 0; timerIdx < numberOfTimers; timerIdx ++) {
                var i = targetId;
                var j = timerIdx;
                var valueName = String.Format($"COMPARELOW_{targetId}_{timerIdx}");
                var lowCompareRegister = new DoubleWordRegister(this, 0xffffffff).WithValueField(0, 32, name: valueName,
                        valueProviderCallback: _ => (uint)(timers[i][j].Compare & 0xFFFF_FFFF),
                        writeCallback: (_, val) => 
                        {
                            var timer = timers[i][j];
                            ulong currentValue = timer.Compare;
                            timer.Compare = currentValue & (ulong)0xFFFF_FFFF_0000_0000 | (ulong) val;
                        });
                this.Log(LogLevel.Noisy, "Add REG: COMPARE_LOWER{0}_{1}[0x{2:X}]", targetId, timerIdx, address + offset);
                registersMap.Add(address + offset + 0x0, lowCompareRegister);
                offset += 4;

                valueName = String.Format($"COMPAREHI_{targetId}_{timerIdx}");
                var hiCompareRegister = new DoubleWordRegister(this, 0xffffffff).WithValueField(0, 32, name: valueName,
                        valueProviderCallback: _ => (uint)(timers[i][j].Compare >> 32),
                        writeCallback: (_, val) => 
                        {
                            var timer = timers[i][j];
                            ulong currentValue = timer.Compare;
                            timer.Compare = (currentValue & (ulong)0x0000_0000_FFFF_FFFF) | (((ulong)val) << 32);                          
                        });
                this.Log(LogLevel.Noisy, "Add REG: COMPARE_UPPER{0}_{1}[0x{2:X}]", targetId, timerIdx, address + offset);
                registersMap.Add(address + offset, hiCompareRegister);
                offset += 4;
            }
            return address + offset;
        }

        private long AddMultiRegisters(Dictionary<long, DoubleWordRegister> registersMap, 
            long address, int targetId, int timerCount, IFlagRegisterField[,] fields, 
            String flagPrefix = "FLAG", String regName = "REG", Action<int, int, bool, bool> multiRegWriteCallback = null,
             Func<int, int, bool> multiRegValueProviderCallback = null) 
        {
            int maximumRegCount = (int)Math.Ceiling((timerCount) / 32.0);
            int offset = 0;
            for(int i = 0; i < maximumRegCount; i++)
            {
                offset = i * 4;
                int bitCount = (timerCount < 32) ? timerCount : 32;
                timerCount -= bitCount;
                var multiReg = new DoubleWordRegister(this, 0x0);
                for (int bitIdx = 0; bitIdx < bitCount; bitIdx++)
                {
                    var flagIdx = i * 32 + bitIdx;
                    var flagName = String.Format("{0}{1}_{2}", flagPrefix, targetId, flagIdx);
                    this.Log(LogLevel.Noisy, $"Add {flagPrefix} bit for {flagIdx} as field {flagName}");
                    multiReg = multiReg.WithFlag(bitIdx, out fields[targetId, flagIdx], name: flagName, writeCallback: (oldValue, newValue) =>
                    {
                        if (multiRegWriteCallback != null) 
                        {
                            multiRegWriteCallback(targetId, flagIdx, oldValue, newValue);
                        }
                    },
                    valueProviderCallback: _ => 
                    {
                        if (multiRegValueProviderCallback != null) 
                        {
                            return multiRegValueProviderCallback(targetId, flagIdx);
                        }
                        return false;
                    });
                }
                this.Log(LogLevel.Noisy, "Add REG: {0}{1}[0x{2:X}]", regName, i, address + offset);
                registersMap.Add(address + offset, multiReg);
            }
            return address + offset + 4;
        }

        private void TimerUpdateDivider(int targetId) 
        {
            for (int i = 0; i < numberOfTimers; i++) 
            {
                uint prescaler = prescalers[targetId].Value + 1;
                uint step = steps[targetId].Value;
                uint divider = prescaler * step;
                timers[targetId][i].Divider = (divider == 0) ? 1 : divider;
            }
        }

        private IFlagRegisterField[] timerEnables;
        private IValueRegisterField[] prescalers;
        private IValueRegisterField[] steps;
        private IFlagRegisterField[,] interruptEnables;
        private IFlagRegisterField[,] interruptStates;
        private IFlagRegisterField[,] interruptTests;
        
        private readonly DoubleWordRegisterCollection registers;

        private int numberOfTargets;
        private int numberOfTimers;
        private int numberOfIrqs;
        private long frequency;
    }
}
