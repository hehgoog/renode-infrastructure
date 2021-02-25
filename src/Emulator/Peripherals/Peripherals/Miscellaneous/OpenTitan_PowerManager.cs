//
// Copyright (c) 2010-2021 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;


namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class OpenTitan_PowerManager : BasicDoubleWordPeripheral, IKnownSize
    {
        public OpenTitan_PowerManager(Machine machine) : base(machine)
        {
            DefineRegisters();
        }

        public long Size =>  0x100;

        private void DefineRegisters()
        {
            Registers.InterruptState.Define(this, 0x0)
                .WithFlag(0, out interruptState, FieldMode.Read | FieldMode.Write,
                    writeCallback: (_, value) =>
                    {
                        this.Log(LogLevel.Info, $"Interrupt State ${value}");
                    }, valueProviderCallback: _ =>
                    {
                        this.Log(LogLevel.Info, "Read Interrupt State");
                        return false;
                    }, name: "wakeup")
                .WithIgnoredBits(1,31);

            Registers.InterruptEnable.Define(this, 0x0)
                .WithFlag(0, out interruptEnable, FieldMode.Read | FieldMode.Write,
                    writeCallback: (_, value) =>
                    {
                        this.Log(LogLevel.Info, $"Interrupt Enable ${value}");
                    }, valueProviderCallback: _ =>
                    {
                        this.Log(LogLevel.Info, "Read Interrupt Enable");
                        return false;
                    }, name: "wakeup")
                .WithIgnoredBits(1,31);

            Registers.InterruptTest.Define(this, 0x0)
                .WithFlag(0, out interruptTest, FieldMode.Read | FieldMode.Write,
                    writeCallback: (_, value) =>
                    {
                        this.Log(LogLevel.Info, $"Interrupt test ${value}");
                    }, valueProviderCallback: _ =>
                    {
                        this.Log(LogLevel.Info, "Read Interrupt test");
                        return false;
                    }, name: "wakeup")
                .WithIgnoredBits(1,31);
        }

        private enum Registers
        {
            InterruptState = 0x0,
            InterruptEnable = 0x4,
            InterruptTest = 0x8,
            ControlConfigRegWriteEnable = 0xc,
            Control = 0x10,
            ConfigClockDomainSync = 0x14,
            WakeupEnableRegWriteEnable = 0x18,
            WakeupEnable = 0x1c,
            WakeStatus = 0x20,
            ResetEnableRegWriteEnable = 0x24,
            ResetEnable = 0x28,
            ResetStatus = 0x2c,
            WakeInfoCaptureDis = 0x30,
            WakeInfo = 0x34,
        }

        private IFlagRegisterField interruptState;
        private IFlagRegisterField interruptEnable;
        private IFlagRegisterField interruptTest;
    }
}

