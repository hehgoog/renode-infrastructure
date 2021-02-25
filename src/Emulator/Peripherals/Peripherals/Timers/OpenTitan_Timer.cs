//
// Copyright (c) 2010-2019 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Timers
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class OpenTitan_Timer : ComparingTimer, IDoubleWordPeripheral, IKnownSize, IRiscVTimeProvider
    {
        public OpenTitan_Timer(Machine machine, long clockFrequency) : base(machine.ClockSource, clockFrequency, limit: ulong.MaxValue, eventEnabled: false, enabled: false)
        {
            stepValue0 = 1;
            prescalerValue0 = 0;
            timerExpired0IRQ = new GPIO();

            CompareReached += () =>
            {
                this.Log(LogLevel.Noisy, "Limit reached, setting timer expired IRQ");
                if (timerInterruptEnable0.Value)
                {
                    timerExpired0IRQ.Set(true);
                }
                else
                {
                    timerExpired0IRQ.Set(false);
                }
            };

            var registersMap = new Dictionary<long, DoubleWordRegister>
           {
                {(long)Registers.Control, new DoubleWordRegister(this)
                    .WithFlag(0, out timerActive0, name: "active_0", changeCallback: (_, value) =>
                             {
                                 string enable_message = value ? "Enabled" : "Disabled";
                                 this.Log(LogLevel.Noisy, $"{enable_message} timer");
                                 Enabled = value;
                             })
                     .WithIgnoredBits(1,31)},

                {(long)Registers.Config0, new DoubleWordRegister(this)
                    .WithValueField(0, 12,  name: "prescaler", writeCallback: (_, value) =>
                             {
                                 prescalerValue0 = value;
                                 Divider = prescalerValue0 + 1;
                                 this.Log(LogLevel.Noisy, $"Config0: Set prescaler to 0x{value:X}");
                             }, valueProviderCallback: _ =>
                             {
                                return prescalerValue0;
                             })
                     .WithValueField(16, 8, name: "step", writeCallback: (_, value) =>
                             {
                                 stepValue0 = value;
                                 this.Log(LogLevel.Noisy, $"Config0: Set step to 0x{value:X}");
                             }, valueProviderCallback: _=>
                             {
                                return stepValue0;
                             })},

                {(long)Registers.TimerValueLower0, new DoubleWordRegister(this)
                     .WithValueField(0, 32, name: "timer value lower", writeCallback: (_, value) =>
                             {
                                timerValue0 &= (ulong)(0xFFFFFFFF<<32);
                                timerValue0 |= value;
                                Value = timerValue0;
                                this.Log(LogLevel.Noisy, $"Set timer0 value lower 0x{value:X} - TimerValue0: 0x{timerValue0:X}");
                             }, valueProviderCallback: _ =>
                             {
                                this.Log(LogLevel.Noisy, $"Get timer0 value lower - TimerValue0: 0x{timerValue0:X}");
                                return (uint)((ulong)Value & (ulong)0xFFFFFFFF);
                             })},

                 {(long)Registers.TimerValueUpper0, new DoubleWordRegister(this)
                     .WithValueField(0, 32, name: "timer value upper", writeCallback: (_, value) =>
                             {
                                timerValue0 &= (ulong)0x00000000_FFFFFFFF;
                                timerValue0 |= ((ulong)value << 32);
                                Value = timerValue0;
                                this.Log(LogLevel.Noisy, $"Set timer0 value upper 0x{value:X} - TimerValue0: 0x{timerValue0:X}");
                             }, valueProviderCallback: _ =>
                             {
                                this.Log(LogLevel.Noisy, $"Get timer0 value upper - TimerValue0: 0x{timerValue0:X}");
                                return (uint)(((ulong)Value >> 32) & (ulong)0x00000000_FFFFFFFF);
                             })},

                 {(long)Registers.CompareLower0, new DoubleWordRegister(this)
                     .WithValueField(0, 32, name: "timer compare lower", writeCallback: (_, value) =>
                             {
                                compareValue0 &= (ulong)0xFFFFFFFF_00000000;
                                compareValue0 |= (ulong)value;
                                Compare = compareValue0;
                                this.Log(LogLevel.Noisy, $"Set timer0 compare lower 0x{value:X} - CompareValue0: 0x{compareValue0:X}");
                             }, valueProviderCallback: _ =>
                             {
                                this.Log(LogLevel.Noisy, $"Get timer0 compare lower - CompareValue0: 0x{compareValue0:X}");
                                return (uint)(compareValue0 & (ulong)0x00000000_FFFFFFFF);
                             })},

                 {(long)Registers.CompareUpper0, new DoubleWordRegister(this)
                 .WithValueField(0, 32, name: "timer compare upper", writeCallback: (_, value) =>
                         {
                            compareValue0 &= (ulong)0x00000000_FFFFFFFF;
                            compareValue0 |= ((ulong)value << 32);
                            Compare = compareValue0;
                            this.Log(LogLevel.Noisy, $"Set timer0 compare upper 0x{value:X} - CompareValue0: 0x{compareValue0:X}");
                         }, valueProviderCallback: _ =>
                         {
                            this.Log(LogLevel.Noisy, $"Get timer0 compare upper - CompareValue0: 0x{compareValue0:X}");
                            return (uint)((compareValue0 >> 32) & (ulong)0x00000000_FFFFFFFF);
                         })},

                 {(long)Registers.InterruptEnable0, new DoubleWordRegister(this)
                 .WithFlag(0, out timerInterruptEnable0, name: "timer0 interrupt enable", changeCallback: (_, value) =>
                         {
                            string enable_message = value ? "Enabled" : "Disabled";
                            this.Log(LogLevel.Noisy, $"{enable_message} timer0 interrupt");
                            EventEnabled = value;
                         })
                 .WithIgnoredBits(1, 31)},

                 {(long)Registers.InterruptState0, new DoubleWordRegister(this)
                 .WithFlag(0, FieldMode.Read | FieldMode.Write, name: "timer0 innerupt state", writeCallback: (_, value) =>
                         {
                            string message = value ? "Request Clear" : "Unset";
                            this.Log(LogLevel.Noisy, $"{message} timer0 interrupt");
                            if (value)
                            {
                                timerExpired0IRQ.Set(false);
                            }
                         }, valueProviderCallback: _ =>
                         {
                            return timerExpired0IRQ.IsSet;
                         })
                 .WithIgnoredBits(1, 31)},
           };
           registers = new DoubleWordRegisterCollection(this, registersMap);

        }

        public override void Reset()
        {
            base.Reset();
            timerExpired0IRQ.Set(false);
        }

        public GPIO IRQ { get; } = new GPIO();

        public long Size => 0x200;

        public ulong TimerValue => Value;

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        private readonly DoubleWordRegisterCollection registers;

        private enum Registers
       {
            Control = 0x0,
            Config0 = 0x100,
            TimerValueLower0 = 0x104,
            TimerValueUpper0 = 0x108,
            CompareLower0 = 0x10c,
            CompareUpper0 = 0x110,
            InterruptEnable0 = 0x114,
            InterruptState0 = 0x118,
            InterruptTest0 = 0x11c,
        }

        private IFlagRegisterField timerActive0;
        private uint prescalerValue0;
        private uint stepValue0;
        private ulong timerValue0;
        private ulong compareValue0;
        private IFlagRegisterField timerInterruptEnable0;

        public GPIO timerExpired0IRQ
	{
            get;
            private set;
        }


    }
}
