//
// Copyright (c) 2010-2021 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers.PLIC;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class OpenTitan_PlatformLevelInterruptController : PlatformLevelInterruptControllerBase, IKnownSize
    {
        public OpenTitan_PlatformLevelInterruptController(int numberOfSources = 84, int numberOfTargets = 1)
            : base(numberOfSources, numberOfTargets, prioritiesEnabled: true, countSourcesFrom0: true, supportedLevels: null)
        {
            // OpenTitan PLIC implementation source is limited
            if (numberOfSources > MaxNumberOfSources) {
                throw new ConstructionException($"Current {this.GetType().Name} implementation does not support more than {MaxNumberOfSources} sources");
            }

            var registersMap = new Dictionary<long, DoubleWordRegister>();
            
            int InterruptPendingRegisterCount = (int)Math.Ceiling(numberOfSources / 32.0);
            int InterruptSourceModeCount = (int)Math.Ceiling(numberOfSources / 32.0);
            for(var i = 0; i < InterruptPendingRegisterCount; i++)
            {
                long InterruptPendingOffset = (long)Registers.InterruptPending0 + i * 4;
                this.Log(LogLevel.Info, "Creating InterruptPending{0}[0x{1:X}]", i, InterruptPendingOffset);
                registersMap.Add((long)InterruptPendingOffset, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: (_) => {
                            // TODO: Build register value from pending sources IRQs
                            return 0;
                        }));
            }

            long InterruptSourceMode0 = (long)Registers.InterruptPending0 + (InterruptPendingRegisterCount * 4);
            for(var i = 0; i < InterruptSourceModeCount; i++)
            {
                long InterruptSourceModeOffset = InterruptSourceMode0 + i *4;
                this.Log(LogLevel.Info, "Creating InterruptSourceMode{0}[0x{1:X}]",i, InterruptSourceModeOffset);
                registersMap.Add(InterruptSourceModeOffset, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read | FieldMode.Write, valueProviderCallback: (_) => {
                            return 0;
                        }, writeCallback: (_, value) => {
                            this.Log(LogLevel.Noisy, $"Write InterruptSourceMode0 0x{value:X}");
                        }));
            }

            long Source0Priority = InterruptSourceMode0 + (InterruptSourceModeCount * 4);
            for(var i = 0; i < numberOfSources; i++)
            {
                var j = i;
                long SourcePriorityOffset = Source0Priority +  (4 * i);
                this.Log(LogLevel.Info, "Creating SourcePriority{0}[0x{1:X}]", i, SourcePriorityOffset);
                registersMap[SourcePriorityOffset] = new DoubleWordRegister(this)
                    .WithValueField(0, 3,
                                    valueProviderCallback: (_) => irqSources[j].Priority,
                                    writeCallback: (_, value) =>
                                    {
                                        irqSources[j].Priority = value;
                                        RefreshInterrupts();
                                    });
            }

            // Algorithm for offset is pulled from the HJSON file within OpenTitan rv_plic
            long Target0MachineEnablesOffset = (long)(0x100*(Math.Ceiling((numberOfSources*4+8*Math.Ceiling(numberOfSources/32.0))/0x100)) + 0*0x100);
            int TargetMachineEnableCount = (int)Math.Ceiling(numberOfSources / 32.0);

            for(var i = 0; i < numberOfTargets; i++)
            {
                long TargetMachineEnablesOffset = Target0MachineEnablesOffset + (TargetMachineEnableCount * i) * 4 + (i * 4 * 3);
                this.Log(LogLevel.Info, $"OFFSET: {TargetMachineEnablesOffset}");
                AddTargetEnablesRegister(registersMap, TargetMachineEnablesOffset, (uint)i, (PrivilegeLevel)0, numberOfSources);

                long TargetClaimCompleteOffset = TargetMachineEnablesOffset + (TargetMachineEnableCount * 4);
                AddTargetClaimCompleteRegister(registersMap, TargetClaimCompleteOffset, (uint)i, (PrivilegeLevel)0);

                long TargetPriorityThresholdOffset = TargetClaimCompleteOffset + 4;
                this.Log(LogLevel.Info, "Creating Target {0} Priority Threshold [0x{1:X}]", i, TargetPriorityThresholdOffset);
                registersMap.Add(TargetPriorityThresholdOffset, new DoubleWordRegister(this)
                    .WithValueField(0, 1, FieldMode.Read | FieldMode.Write, valueProviderCallback: (_) => {
                            return 0;
                        }, writeCallback: (_, value) => {
                            var j = i;
                            this.Log(LogLevel.Noisy, $"Write Target {j} Priority Threshold 0x{value:X}");
                        }));
                    
                long TargetSoftwareOffset = TargetPriorityThresholdOffset + 4;
                this.Log(LogLevel.Info, "Creating Target {0} Software Interrupt [0x{1:X}]", i, TargetSoftwareOffset);
                registersMap.Add(TargetSoftwareOffset, new DoubleWordRegister(this)
                    .WithValueField(0, 1, FieldMode.Read | FieldMode.Write, valueProviderCallback: (_) => {
                            return 0;
                        }, writeCallback: (_, value) => {
                            var j = i;
                            this.Log(LogLevel.Noisy, $"Write Target {j} Software Interrupt 0x{value:X}");
                        }));

            }

            registers = new DoubleWordRegisterCollection(this, registersMap);
        }

        public long Size => 0x1000;

        private const int MaxNumberOfSources = 255;

        private enum Registers : long
        {
            InterruptPending0 = 0x0,


            Target0MachineEnables = 0x200,
            Target0PriorityThreshold = 0x20C,
            Target0ClaimComplete = 0x210,

            Target0SoftwareInterrupt = 0x214,
        }
    }
}
