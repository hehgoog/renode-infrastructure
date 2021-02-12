using System;
using System.Linq;
using System.Collections.Generic;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.MTD
{
    public class OpenTitan_FlashController : IDoubleWordPeripheral, IKnownSize
    {
        enum ControlOp : uint
        {
            FLASH_READ = 0x0,
            FLASH_PROGRAM = 0x1,
            FLASH_ERASE = 0x2
        }

        enum ControlPartitionSel : uint
        {
            DATA_PARTITION = 0x0,
            INFO_PARTITION = 0x1
        }

        enum ControlEraseSel : uint
        {
            PAGE_ERASE = 0x0,
            BANK_ERASE = 0x1
        }

        enum OperationState{
            INIT, WAITING, RUNNING, DONE
        }

        enum OperationType{
            READ_DATA, PROGRAM_DATA, ERASE_DATA_PAGE
        }

        public OpenTitan_FlashController(Machine machine, MappedMemory flash)
        {
            this.Log(LogLevel.Info, "OpenTitan_FlashController/OpenTitan_FlashController: Entered");
            var registersMap = new Dictionary<long, DoubleWordRegister>
            {
                // TODO(julianmb): support interrupts. no unittests exist though.
                {(long)Registers.INTR_STATE, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "prog_empty", mode: FieldMode.Read | FieldMode.WriteOneToClear)
                    .WithFlag(1, name: "prog_lvl", mode: FieldMode.Read | FieldMode.WriteOneToClear)
                    .WithFlag(2, name: "rd_full", mode: FieldMode.Read | FieldMode.WriteOneToClear)
                    .WithFlag(3, name: "rd_lvl", mode: FieldMode.Read | FieldMode.WriteOneToClear)
                    .WithFlag(4, out interuptStatusRegisterOpDoneFlag, name: "op_done",
                        mode: FieldMode.Read | FieldMode.WriteOneToClear)
                    .WithFlag(5, out interruptStatusRegisterOpErrorFlag, name: "op_error",
                        mode: FieldMode.Read | FieldMode.WriteOneToClear)
                    .WithIgnoredBits(6, 1 + 31 - 6)
                },
                // TODO(julianmb): support interrupts. no unittests exist though.
                {(long)Registers.INTR_ENABLE, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "prog_empty")
                    .WithFlag(1, name: "prog_lvl")
                    .WithFlag(2, name: "rd_full")
                    .WithFlag(3, name: "rd_lvl")
                    .WithFlag(4, name: "op_done")
                    .WithFlag(5, name: "op_error")
                    .WithIgnoredBits(6, 1 + 31 - 6)
                },
                // TODO(julianmb): support interrupts. no unittests exist though.
                {(long)Registers.INTR_TEST, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "prog_empty", mode: FieldMode.Write)
                    .WithFlag(1, name: "prog_lvl", mode: FieldMode.Write)
                    .WithFlag(2, name: "rd_full", mode: FieldMode.Write)
                    .WithFlag(3, name: "rd_lvl", mode: FieldMode.Write)
                    .WithFlag(4, name: "op_done", mode: FieldMode.Write)
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                // TODO(julianmb): support register write enable. this isnt tested in the unittests currently
                {(long)Registers.CTRL_REGWEN, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "EN", mode: FieldMode.Read)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.CONTROL, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "START", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.CONTROL/START written old = {0}, new = {1}",
                            o, n);
                        if(n){
                            opState = OperationState.RUNNING;
                            StartOperation();
                        }
                    })
                    .WithReservedBits(1, 3)
                    .WithValueField(4, 2, name: "OP", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.CONTROL/OP written old = 0x{0:X}, new = 0x{1:X} ({2})",
                            o, n, ((ControlOp)n).ToString("g"));
                    })
                    .WithFlag(6, name: "PROG_SEL", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.CONTROL/PROG_SEL written old = {0}, new = {1}",
                            o, n);
                    })
                    .WithFlag(7, name: "ERASE_SEL", writeCallback: (o, n) => {
                        uint oUint = (uint)(o ? 0x1 : 0x0);
                        uint nUint = (uint)(n ? 0x1 : 0x0);
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.CONTROL/ERASE_SEL written old = {0} ({1}), new = {2} ({3})",
                            o, ((ControlEraseSel) oUint).ToString("g"),
                            n, ((ControlEraseSel) nUint).ToString("g"));
                    })
                    .WithFlag(8, name: "PARTITION_SEL", writeCallback: (o, n) => {
                        uint oUint = (uint)(o ? 0x1 : 0x0);
                        uint nUint = (uint)(n ? 0x1 : 0x0);
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.CONTROL/PARTITION_SEL written old = {0} ({1}), new = {2} ({3})",
                            o, ((ControlPartitionSel) oUint).ToString("g"),
                            n, ((ControlPartitionSel) nUint).ToString("g"));
                    })
                    .WithValueField(9, 2, name: "INFO_SEL", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.CONTROL/INFO_SEL written old = 0x{0:X} , new = 0x{1:X}",
                            o, n);
                    })
                    .WithIgnoredBits(11, 1 + 15 - 11)
                    .WithValueField(16, 1 + 27 - 16, name: "NUM", writeCallback: (_, num) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.CONTROL/NUM written NUM = {0}",
                            num);
                    })
                    .WithIgnoredBits(28, 1 + 31 - 28)
                },
                {(long)Registers.ADDR, new DoubleWordRegister(this, 0x0)
                    .WithValueField(0, 32, name: "START", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.ADDR/START written old = 0x{0:X}, new = 0x{1:X}",
                            o, n);
                    })
                },
                // TODO(julianmb): support register write enable. this isnt tested in the unittests currently
                {(long)Registers.REGION_CFG_REGWEN_0, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_0", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.REGION_CFG_REGWEN_1, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_1", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.REGION_CFG_REGWEN_2, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_2", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.REGION_CFG_REGWEN_3, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_3", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.REGION_CFG_REGWEN_4, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_4", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.REGION_CFG_REGWEN_5, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_5", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.REGION_CFG_REGWEN_6, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_6", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.REGION_CFG_REGWEN_7, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_7", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.MP_REGION_CFG_0, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_0")
                    .WithFlag(1, name: "RD_EN_0")
                    .WithFlag(2, name: "PROG_EN_0")
                    .WithFlag(3, name: "ERASE_EN_0")
                    .WithFlag(4, name: "SCRAMBLE_EN_0")
                    .WithIgnoredBits(5, 1 + 7 - 5)
                    .WithValueField(8, 1 + 16 - 8, name: "BASE_0")
                    .WithIgnoredBits(17, 1 + 19 - 17)
                    .WithValueField(20, 1 + 29 - 20, name: "SIZE_0")
                    .WithIgnoredBits(30, 1 + 31 - 30)
                },
                {(long)Registers.MP_REGION_CFG_1, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_1")
                    .WithFlag(1, name: "RD_EN_1")
                    .WithFlag(2, name: "PROG_EN_1")
                    .WithFlag(3, name: "ERASE_EN_1")
                    .WithFlag(4, name: "SCRAMBLE_EN_1")
                    .WithIgnoredBits(5, 1 + 7 - 5)
                    .WithValueField(8, 1 + 16 - 8, name: "BASE_1")
                    .WithIgnoredBits(17, 1 + 19 - 17)
                    .WithValueField(20, 1 + 29 - 20, name: "SIZE_1")
                    .WithIgnoredBits(30, 1 + 31 - 30)
                },
                {(long)Registers.MP_REGION_CFG_2, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_2")
                    .WithFlag(1, name: "RD_EN_2")
                    .WithFlag(2, name: "PROG_EN_2")
                    .WithFlag(3, name: "ERASE_EN_2")
                    .WithFlag(4, name: "SCRAMBLE_EN_2")
                    .WithIgnoredBits(5, 1 + 7 - 5)
                    .WithValueField(8, 1 + 16 - 8, name: "BASE_2")
                    .WithIgnoredBits(17, 1 + 19 - 17)
                    .WithValueField(20, 1 + 29 - 20, name: "SIZE_2")
                    .WithIgnoredBits(30, 1 + 31 - 30)
                },
                {(long)Registers.MP_REGION_CFG_3, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_3")
                    .WithFlag(1, name: "RD_EN_3")
                    .WithFlag(2, name: "PROG_EN_3")
                    .WithFlag(3, name: "ERASE_EN_3")
                    .WithFlag(4, name: "SCRAMBLE_EN_3")
                    .WithIgnoredBits(5, 1 + 7 - 5)
                    .WithValueField(8, 1 + 16 - 8, name: "BASE_3")
                    .WithIgnoredBits(17, 1 + 19 - 17)
                    .WithValueField(20, 1 + 29 - 20, name: "SIZE_3")
                    .WithIgnoredBits(30, 1 + 31 - 30)
                },
                {(long)Registers.MP_REGION_CFG_4, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_4")
                    .WithFlag(1, name: "RD_EN_4")
                    .WithFlag(2, name: "PROG_EN_4")
                    .WithFlag(3, name: "ERASE_EN_4")
                    .WithFlag(4, name: "SCRAMBLE_EN_4")
                    .WithIgnoredBits(5, 1 + 7 - 5)
                    .WithValueField(8, 1 + 16 - 8, name: "BASE_4")
                    .WithIgnoredBits(17, 1 + 19 - 17)
                    .WithValueField(20, 1 + 29 - 20, name: "SIZE_4")
                    .WithIgnoredBits(30, 1 + 31 - 30)
                },
                {(long)Registers.MP_REGION_CFG_5, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_5")
                    .WithFlag(1, name: "RD_EN_5")
                    .WithFlag(2, name: "PROG_EN_5")
                    .WithFlag(3, name: "ERASE_EN_5")
                    .WithFlag(4, name: "SCRAMBLE_EN_5")
                    .WithIgnoredBits(5, 1 + 7 - 5)
                    .WithValueField(8, 1 + 16 - 8, name: "BASE_5")
                    .WithIgnoredBits(17, 1 + 19 - 17)
                    .WithValueField(20, 1 + 29 - 20, name: "SIZE_5")
                    .WithIgnoredBits(30, 1 + 31 - 30)
                },
                {(long)Registers.MP_REGION_CFG_6, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_6")
                    .WithFlag(1, name: "RD_EN_6")
                    .WithFlag(2, name: "PROG_EN_6")
                    .WithFlag(3, name: "ERASE_EN_6")
                    .WithFlag(4, name: "SCRAMBLE_EN_6")
                    .WithIgnoredBits(5, 1 + 7 - 5)
                    .WithValueField(8, 1 + 16 - 8, name: "BASE_6")
                    .WithIgnoredBits(17, 1 + 19 - 17)
                    .WithValueField(20, 1 + 29 - 20, name: "SIZE_6")
                    .WithIgnoredBits(30, 1 + 31 - 30)
                },
                {(long)Registers.MP_REGION_CFG_7, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_7")
                    .WithFlag(1, name: "RD_EN_7")
                    .WithFlag(2, name: "PROG_EN_7")
                    .WithFlag(3, name: "ERASE_EN_7")
                    .WithFlag(4, name: "SCRAMBLE_EN_7")
                    .WithIgnoredBits(5, 1 + 7 - 5)
                    .WithValueField(8, 1 + 16 - 8, name: "BASE_7")
                    .WithIgnoredBits(17, 1 + 19 - 17)
                    .WithValueField(20, 1 + 29 - 20, name: "SIZE_7")
                    .WithIgnoredBits(30, 1 + 31 - 30)
                },
                // TODO(julianmb): support register write enable. this isnt tested in the unittests currently
                {(long)Registers.BANK0_INFO0_REGWEN_0, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_0", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK0_INFO0_REGWEN_1, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_1", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK0_INFO0_REGWEN_2, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_2", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK0_INFO0_REGWEN_3, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_3", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK0_INFO1_REGWEN_0, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_0", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK0_INFO1_REGWEN_1, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_1", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK0_INFO1_REGWEN_2, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_2", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK0_INFO1_REGWEN_3, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_3", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK0_INFO0_PAGE_CFG_0, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_0")
                    .WithFlag(1, name: "RD_EN_0")
                    .WithFlag(2, name: "PROG_EN_0")
                    .WithFlag(3, name: "ERASE_EN_0")
                    .WithFlag(4, name: "SCRAMBLE_EN_0")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK0_INFO0_PAGE_CFG_1, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_1")
                    .WithFlag(1, name: "RD_EN_1")
                    .WithFlag(2, name: "PROG_EN_1")
                    .WithFlag(3, name: "ERASE_EN_1")
                    .WithFlag(4, name: "SCRAMBLE_EN_1")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK0_INFO0_PAGE_CFG_2, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_2")
                    .WithFlag(1, name: "RD_EN_2")
                    .WithFlag(2, name: "PROG_EN_2")
                    .WithFlag(3, name: "ERASE_EN_2")
                    .WithFlag(4, name: "SCRAMBLE_EN_2")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK0_INFO0_PAGE_CFG_3, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_3")
                    .WithFlag(1, name: "RD_EN_3")
                    .WithFlag(2, name: "PROG_EN_3")
                    .WithFlag(3, name: "ERASE_EN_3")
                    .WithFlag(4, name: "SCRAMBLE_EN_3")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK0_INFO1_PAGE_CFG_0, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_0")
                    .WithFlag(1, name: "RD_EN_0")
                    .WithFlag(2, name: "PROG_EN_0")
                    .WithFlag(3, name: "ERASE_EN_0")
                    .WithFlag(4, name: "SCRAMBLE_EN_0")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK0_INFO1_PAGE_CFG_1, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_1")
                    .WithFlag(1, name: "RD_EN_1")
                    .WithFlag(2, name: "PROG_EN_1")
                    .WithFlag(3, name: "ERASE_EN_1")
                    .WithFlag(4, name: "SCRAMBLE_EN_1")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK0_INFO1_PAGE_CFG_2, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_2")
                    .WithFlag(1, name: "RD_EN_2")
                    .WithFlag(2, name: "PROG_EN_2")
                    .WithFlag(3, name: "ERASE_EN_2")
                    .WithFlag(4, name: "SCRAMBLE_EN_2")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK0_INFO1_PAGE_CFG_3, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_3")
                    .WithFlag(1, name: "RD_EN_3")
                    .WithFlag(2, name: "PROG_EN_3")
                    .WithFlag(3, name: "ERASE_EN_3")
                    .WithFlag(4, name: "SCRAMBLE_EN_3")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                // TODO(julianmb): support register write enable. this isnt tested in the unittests currently
                {(long)Registers.BANK1_INFO0_REGWEN_0, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_0", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK1_INFO0_REGWEN_1, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_1", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK1_INFO0_REGWEN_2, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_2", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK1_INFO0_REGWEN_3, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_3", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK1_INFO1_REGWEN_0, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_0", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK1_INFO1_REGWEN_1, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_1", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK1_INFO1_REGWEN_2, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_2", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK1_INFO1_REGWEN_3, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "REGION_3", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.BANK1_INFO0_PAGE_CFG_0, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_0")
                    .WithFlag(1, name: "RD_EN_0")
                    .WithFlag(2, name: "PROG_EN_0")
                    .WithFlag(3, name: "ERASE_EN_0")
                    .WithFlag(4, name: "SCRAMBLE_EN_0")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK1_INFO0_PAGE_CFG_1, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_1")
                    .WithFlag(1, name: "RD_EN_1")
                    .WithFlag(2, name: "PROG_EN_1")
                    .WithFlag(3, name: "ERASE_EN_1")
                    .WithFlag(4, name: "SCRAMBLE_EN_1")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK1_INFO0_PAGE_CFG_2, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_2")
                    .WithFlag(1, name: "RD_EN_2")
                    .WithFlag(2, name: "PROG_EN_2")
                    .WithFlag(3, name: "ERASE_EN_2")
                    .WithFlag(4, name: "SCRAMBLE_EN_2")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK1_INFO0_PAGE_CFG_3, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_3")
                    .WithFlag(1, name: "RD_EN_3")
                    .WithFlag(2, name: "PROG_EN_3")
                    .WithFlag(3, name: "ERASE_EN_3")
                    .WithFlag(4, name: "SCRAMBLE_EN_3")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK1_INFO1_PAGE_CFG_0, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_0")
                    .WithFlag(1, name: "RD_EN_0")
                    .WithFlag(2, name: "PROG_EN_0")
                    .WithFlag(3, name: "ERASE_EN_0")
                    .WithFlag(4, name: "SCRAMBLE_EN_0")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK1_INFO1_PAGE_CFG_1, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_1")
                    .WithFlag(1, name: "RD_EN_1")
                    .WithFlag(2, name: "PROG_EN_1")
                    .WithFlag(3, name: "ERASE_EN_1")
                    .WithFlag(4, name: "SCRAMBLE_EN_1")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK1_INFO1_PAGE_CFG_2, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_2")
                    .WithFlag(1, name: "RD_EN_2")
                    .WithFlag(2, name: "PROG_EN_2")
                    .WithFlag(3, name: "ERASE_EN_2")
                    .WithFlag(4, name: "SCRAMBLE_EN_2")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.BANK1_INFO1_PAGE_CFG_3, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN_3")
                    .WithFlag(1, name: "RD_EN_3")
                    .WithFlag(2, name: "PROG_EN_3")
                    .WithFlag(3, name: "ERASE_EN_3")
                    .WithFlag(4, name: "SCRAMBLE_EN_3")
                    .WithIgnoredBits(5, 1 + 31 - 5)
                },
                {(long)Registers.DEFAULT_REGION, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "RD_EN", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.DEFAULT_REGION/RD_EN written old = {0}, new = {1}",
                            o, n);
                    })
                    .WithFlag(1, name: "PROG_EN", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.DEFAULT_REGION/PROG_EN written old = {0}, new = {1}",
                            o, n);
                    })
                    .WithFlag(2, name: "ERASE_EN", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.DEFAULT_REGION/ERASE_EN written old = {0}, new = {1}",
                            o, n);
                    })
                    .WithFlag(3, name: "SCRAMBLE_EN", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.DEFAULT_REGION/SCRAMBLE_EN written old = {0}, new = {1}",
                            o, n);
                    })
                    .WithIgnoredBits(4, 1 + 31 - 4)
                },
                // TODO(julianmb): support register write enable. this isnt tested in the unittests currently
                {(long)Registers.BANK_CFG_REGWEN, new DoubleWordRegister(this, 0x1)
                    .WithFlag(0, name: "BANK", mode: FieldMode.Read | FieldMode.WriteZeroToClear)
                    .WithIgnoredBits(1, 31)
                },
                {(long)Registers.MP_BANK_CFG, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "ERASE_EN_0")
                    .WithFlag(1, name: "ERASE_EN_1")
                    .WithIgnoredBits(2, 1 + 31 - 2)
                },
                {(long)Registers.OP_STATUS, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, out opStatusRegisterDoneFlag, name: "done", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.OP_STATUS/done written old = {0}, new = {1}",
                            o, n);
                        if(!n){
                            opState = OperationState.WAITING;
                        }
                    })
                    .WithFlag(1, out opStatusRegisterErrorFlag, name: "err", writeCallback: (o, n) => {
                        this.Log(LogLevel.Noisy,
                            "OpenTitan_FlashController/Registers.OP_STATUS/err written old = {0}, new = {1}",
                            o, n);
                    })
                    .WithIgnoredBits(2, 1 + 31 - 2)
                },
                {(long)Registers.STATUS, new DoubleWordRegister(this, 0xa)
                    .WithFlag(0, name: "rd_full", mode: FieldMode.Read, valueProviderCallback: (_) => {
                        return readFifo.Count >= readFifoDepth;
                    })
                    .WithFlag(1, name: "rd_empty", mode: FieldMode.Read, valueProviderCallback: (_) => {
                        return readFifo.Count == 0;
                    })
                    .WithFlag(2, name: "prog_full", mode: FieldMode.Read, valueProviderCallback: (_) => {
                        return programFifo.Count >= programFifoDepth;
                    })
                    .WithFlag(3, name: "prog_empty", mode: FieldMode.Read, valueProviderCallback: (_) => {
                        return programFifo.Count == 0;
                    })
                    .WithFlag(4, name: "init_wip", mode: FieldMode.Read)
                    .WithIgnoredBits(5, 1 + 7 - 5)
                    .WithValueField(8, 1 + 16 - 8, name: "error_addr", mode: FieldMode.Read)
                    .WithIgnoredBits(17, 1 + 31 - 17)
                },
                {(long)Registers.PHY_STATUS, new DoubleWordRegister(this, 0x6)
                    .WithFlag(0, name: "init_wip", mode: FieldMode.Read)
                    .WithFlag(1, name: "prog_normal_avail", mode: FieldMode.Read)
                    .WithFlag(2, name: "prog_repair_avail", mode: FieldMode.Read)
                    .WithIgnoredBits(3, 1 + 31 - 3)
                },
                {(long)Registers.Scratch, new DoubleWordRegister(this, 0x0)
                    .WithValueField(0, 32, name: "data")
                },
                {(long)Registers.FIFO_LVL, new DoubleWordRegister(this, 0xf0f)
                    .WithValueField(0, 5, name: "PROG")
                    .WithReservedBits(5, 1 + 7 - 5)
                    .WithValueField(8, 1 + 12 - 8, name: "RD")
                    .WithIgnoredBits(13, 1 + 31 - 13)
                },
                // TODO(julianmb): implement fifo reset. There isnt any unittest for this currently.
                {(long)Registers.FIFO_RST, new DoubleWordRegister(this, 0x0)
                    .WithFlag(0, name: "EN")
                    .WithIgnoredBits(1, 31)
                },
                // TODO(julianmb): handle writes while fifo is full. There isnt any unittest for this currently.
                {(long)Registers.prog_fifo, new DoubleWordRegister(this, 0x0)
                    .WithValueField(0, 32, mode: FieldMode.Write, writeCallback: (_, n) => {
                        if(programFifo.Count >= programFifoDepth && !supressFullProgramFifoWarning){
                            this.Log(LogLevel.Warning,
                                "OpenTitan_FlashController/Registers.prog_fifo: writing to an already full fifo. " +
                                "Accepting the write(RTL will behave differently)!");
                            supressFullProgramFifoWarning = true;
                        }
                        programFifo.Enqueue(n);
                    })
                },
                {(long)Registers.rd_fifo, new DoubleWordRegister(this, 0x0)
                    .WithValueField(0, 32, mode: FieldMode.Read, valueProviderCallback: (_) => {
                        if(readFifo.Count == 0){
                            this.Log(LogLevel.Warning,
                                "OpenTitan_FlashController/Registers.rd_fifo: reading an empty fifo. RETURNING A DUMMY VALUE!");
                        }
                        return readFifo.Count == 0 ? 0xDEADBEEF : readFifo.Dequeue();
                    })
                }
            }; // var registersMap
            registersCollection = new DoubleWordRegisterCollection(this, registersMap);
            this.flash = flash;
            infoFlash = new MappedMemory(machine, flash.Size);
            workQueue = new Queue<FlashControllerWorkItem>();
            readFifo = new Queue<uint>((int)readFifoDepth);
            programFifo = new Queue<uint>((int)programFifoDepth);
            supressFullProgramFifoWarning = false;
            this.Reset();
        }

        public void Reset(){
            this.Log(LogLevel.Info, "OpenTitan_FlashController/Reset");
            registersCollection.Reset();
            workQueue.Clear();
            readFifo.Clear();
            programFifo.Clear();
            opState = OperationState.INIT;
            supressFullProgramFifoWarning = false;
        }

        public uint ReadDoubleWord(long offset){
            DoWork();
            uint value = registersCollection.Read(offset);
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/ReadDoubleWord: offset = 0x{0:X}, value = 0x{1:X}",
                offset, value);
            return value;
        }

        public void WriteDoubleWord(long offset, uint value){
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/WriteDoubleWord: offset = 0x{0:X}, value = 0x{1:X}",
                offset, value);
            registersCollection.Write(offset, value);
        }

        private void StartOperation(){
            uint CONTROL_VALUE = registersCollection.Read((long)Registers.CONTROL);
            uint CONTROL_OP_VALUE = 0x3 & (CONTROL_VALUE >> 4);

            bool flashReadOp = CONTROL_OP_VALUE == (uint)ControlOp.FLASH_READ;
            bool flashProgramOp = CONTROL_OP_VALUE == (uint)ControlOp.FLASH_PROGRAM;
            bool flashEraseOp = CONTROL_OP_VALUE == (uint)ControlOp.FLASH_ERASE;

            if(flashReadOp){
                this.Log(
                    LogLevel.Info,
                    "OpenTitan_FlashController/StartOperation: Read");
                StartReadOperation();
            }else if(flashProgramOp){
                this.Log(
                    LogLevel.Info,
                    "OpenTitan_FlashController/StartOperation: Program");
                StartProgramOperation();
            }else if(flashEraseOp){
                this.Log(
                    LogLevel.Info,
                    "OpenTitan_FlashController/StartOperation: Erase");
                StartEraseOperation();
            }else{
                this.Log(
                    LogLevel.Warning,
                    "OpenTitan_FlashController/StartOperation: invalid CONTROL_OP value");
            }

            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/StartOperation: workQueue.Count = {0}", workQueue.Count);
        }

        private void StartReadOperation(){
            uint ADDR_VALUE = registersCollection.Read((long)Registers.ADDR);

            uint CONTROL_VALUE = registersCollection.Read((long)Registers.CONTROL);
            uint partitionSelFlag = 0x1 & (CONTROL_VALUE >> 8);
            bool flashReadDataPartition = partitionSelFlag == (uint)ControlPartitionSel.DATA_PARTITION;
            bool flashReadInfoPartition = partitionSelFlag == (uint)ControlPartitionSel.INFO_PARTITION;

            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/StartReadOperation: flashReadDataPartition = {0}, flashReadInfoPartition = {1}",
                flashReadDataPartition, flashReadInfoPartition);

            uint CONTROL_NUM_VALUE = 0x00000FFF & (CONTROL_VALUE >> 16);

            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/StartReadOperation: CONTROL_NUM_VALUE = {0}",
                CONTROL_NUM_VALUE);

            for (long i = 0 ; i < CONTROL_NUM_VALUE + 1 ; i++){
                if(flashReadDataPartition){
                    workQueue.Enqueue(new FlashControllerWorkItem().WithDataRead(ADDR_VALUE + (4 * i)));
                }
                if(flashReadInfoPartition){
                    workQueue.Enqueue(new FlashControllerWorkItem().WithInfoRead(ADDR_VALUE + (4 * i)));
                }
            }
        }

        private void StartProgramOperation(){
            uint ADDR_VALUE = registersCollection.Read((long)Registers.ADDR);

            uint CONTROL_VALUE = registersCollection.Read((long)Registers.CONTROL);
            uint partitionSelFlag = 0x1 & (CONTROL_VALUE >> 8);
            bool flashProgramDataPartition = partitionSelFlag == (uint)ControlPartitionSel.DATA_PARTITION;
            bool flashProgramInfoPartition = partitionSelFlag == (uint)ControlPartitionSel.INFO_PARTITION;

            uint CONTROL_NUM_VALUE = 0x00000FFF & (CONTROL_VALUE >> 16);

            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/StartProgramOperation: ADDR_VALUE = 0x{0:X}",
                ADDR_VALUE);

            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/StartProgramOperation: flashProgramDataPartition = {0}, flashProgramInfoPartition = {1}",
                flashProgramDataPartition, flashProgramInfoPartition);

            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/StartProgramOperation: CONTROL_NUM_VALUE = {0}",
                CONTROL_NUM_VALUE);

            for (long i = 0 ; i < CONTROL_NUM_VALUE + 1 ; i++){
                if(flashProgramDataPartition){
                    workQueue.Enqueue(new FlashControllerWorkItem().WithDataProgram(ADDR_VALUE + (4 * i)));
                }
                if(flashProgramInfoPartition){
                    workQueue.Enqueue(new FlashControllerWorkItem().WithInfoProgram(ADDR_VALUE + (4 * i)));
                }
            }
        }

        private void StartEraseOperation(){
            uint CONTROL_VALUE = registersCollection.Read((long)Registers.CONTROL);

            uint eraseSelFlag = 0x1 & (CONTROL_VALUE >> 7);
            bool flashEraseBank = eraseSelFlag == (uint)ControlEraseSel.BANK_ERASE;
            bool flashErasePage = eraseSelFlag == (uint)ControlEraseSel.PAGE_ERASE;

            uint partitionSelFlag = 0x1 & (CONTROL_VALUE >> 8);
            bool flashEraseDataPartition = flashErasePage && (partitionSelFlag == (uint)ControlPartitionSel.DATA_PARTITION);
            bool flashEraseInfoPartition = flashErasePage && (partitionSelFlag == (uint)ControlPartitionSel.INFO_PARTITION);

            uint MP_BANK_CFG_VALUE = registersCollection.Read((long)Registers.MP_BANK_CFG);
            bool flashEraseBank0 = flashEraseBank && 0x1 == (0x1 & MP_BANK_CFG_VALUE);
            bool flashEraseBank1 = flashEraseBank && 0x1 == (0x1 & (MP_BANK_CFG_VALUE >> 1));

            if(flashErasePage){
                uint ADDR_VALUE = registersCollection.Read((long)Registers.ADDR);
                this.Log(
                    LogLevel.Noisy,
                    "OpenTitan_FlashController/StartEraseOperation: ADDR_VALUE = 0x{0:X}",
                    ADDR_VALUE);
                if(flashEraseDataPartition){
                    workQueue.Enqueue(new FlashControllerWorkItem().WithDataPageErase((long) ADDR_VALUE));
                }else if(flashEraseInfoPartition){
                    workQueue.Enqueue(new FlashControllerWorkItem().WithInfoPageErase((long) ADDR_VALUE));
                }
            }

            if(flashEraseBank0){
                workQueue.Enqueue(new FlashControllerWorkItem().WithBankErase(0));
            }

            if(flashEraseBank1){
                workQueue.Enqueue(new FlashControllerWorkItem().WithBankErase(1));
            }
        }

        private void DoWork(){
            if(workQueue.Count == 0){
                if(opState == OperationState.DONE){
                    opStatusRegisterDoneFlag.Value = true;
                    interuptStatusRegisterOpDoneFlag.Value = true;
                }else if(opState == OperationState.RUNNING){
                    opState = OperationState.DONE;
                }
                return;
            }
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoWork: workQueue.Count = {0}", workQueue.Count);
            FlashControllerWorkItem workItem = workQueue.Dequeue();
            if(workItem.eraseBank){
                DoEraseBank(workItem.eraseBankNumber);
            }else if(workItem.eraseDataPage){
                DoEraseDataPage(workItem.erasePageAddress);
            }else if(workItem.eraseInfoPage){
                DoEraseInfoPage(workItem.erasePageAddress);
            }else if(workItem.readData){
                DoReadData(workItem.readAddress);
            }else if(workItem.readInfo){
                DoReadInfo(workItem.readAddress);
            }else if(workItem.programData){
                DoProgramData(workItem.programAddress);
            }else if(workItem.programInfo){
                DoProgramInfo(workItem.programAddress);
            }else{
                this.Log(
                    LogLevel.Warning,
                    "OpenTitan_FlashController/DoWork: Unhandled workItem");
            }
        }

        private void DoEraseBank(uint bankNumber){
            /*
            ** TODO(julianmb): memory protection should be implemented.
            ** It currently doesnt have a unit test though.
            ** MP_BANK_CFG register is of interest
            */
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoEraseBank: bankNumber = {0}",
                bankNumber);

            long bytesPerBank = FLASH_PAGES_PER_BANK * FLASH_WORDS_PER_PAGE * FLASH_WORD_SZ;
            long bankEflashOffset = bankNumber * bytesPerBank;

            for(long i = 0 ; i < bytesPerBank ; i++){
                flash.WriteByte(bankEflashOffset + i, 0xff);
            }
        }

        private void DoEraseDataPage(long pageAddress){
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoEraseDataPage: pageAddress = 0x{0:X}",
                pageAddress);
            if(IsOperationAllowed(OperationType.ERASE_DATA_PAGE, pageAddress)){
                long pageOffset = pageAddress - FLASH_MEM_BASE_ADDR;
                long bytesPerPage = FLASH_WORDS_PER_PAGE * FLASH_WORD_SZ;
                for(long i = 0 ; i < bytesPerPage ; i++){
                    flash.WriteByte(pageOffset + i, 0xff);
                }
            }else{
                interruptStatusRegisterOpErrorFlag.Value |= true;
                opStatusRegisterErrorFlag.Value |= true;
            }
        }

        private void DoEraseInfoPage(long pageAddress){
            /*
            ** TODO(julianmb): memory protection should be implemented.
            ** It currently doesnt have a unit test though.
            ** BANK*_INFO*_PAGE_CFG_* registers are of interest
            */
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoEraseInfoPage: pageAddress = 0x{0:X}",
                pageAddress);
            long pageOffset = pageAddress - FLASH_MEM_BASE_ADDR;
            long bytesPerPage = FLASH_WORDS_PER_PAGE * FLASH_WORD_SZ;
            for(long i = 0 ; i < bytesPerPage ; i++){
                infoFlash.WriteByte(pageOffset + i, 0xff);
            }
        }

        private void DoReadData(long busWordAddress){
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoReadData: busWordAddress = 0x{0:X}",
                busWordAddress);
            uint value = flash.ReadDoubleWord(busWordAddress - FLASH_MEM_BASE_ADDR);
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoReadData: value = 0x{0:X}",
                value);
            if(IsOperationAllowed(OperationType.READ_DATA, busWordAddress)){
                readFifo.Enqueue(value);
            }else{
                interruptStatusRegisterOpErrorFlag.Value |= true;
                opStatusRegisterErrorFlag.Value |= true;
            }
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoReadData: readFifo.Count = {0}",
                readFifo.Count);
        }

        private void DoReadInfo(long busWordAddress){
            /*
            ** TODO(julianmb): memory protection should be implemented.
            ** It currently doesnt have a unit test though.
            ** BANK*_INFO*_PAGE_CFG_* registers are of interest
            */
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoReadInfo: busWordAddress = 0x{0:X}",
                busWordAddress);
            uint value = infoFlash.ReadDoubleWord(busWordAddress - FLASH_MEM_BASE_ADDR);
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoReadInfo: value = 0x{0:X}",
                value);
            readFifo.Enqueue(value);
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoReadInfo: readFifo.Count = {0}",
                readFifo.Count);
        }

        private void DoProgramData(long busWordAddress){
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoProgramData: busWordAddress = 0x{0:X}",
                busWordAddress);
            uint value = programFifo.Dequeue();
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoProgramData: value = 0x{0:X}",
                value);
            if(IsOperationAllowed(OperationType.PROGRAM_DATA, busWordAddress)){
                flash.WriteDoubleWord(busWordAddress - FLASH_MEM_BASE_ADDR, value);
            }else{
                interruptStatusRegisterOpErrorFlag.Value |= true;
                opStatusRegisterErrorFlag.Value |= true;
            }

            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoProgramData: programFifo.Count = {0}",
                programFifo.Count);
        }

        private void DoProgramInfo(long busWordAddress){
            /*
            ** TODO(julianmb): memory protection should be implemented.
            ** It currently doesnt have a unit test though.
            ** BANK*_INFO*_PAGE_CFG_* registers are of interest
            */
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoProgramInfo: busWordAddress = 0x{0:X}",
                busWordAddress);
            uint value = programFifo.Dequeue();
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoProgramInfo: value = 0x{0:X}",
                value);
            infoFlash.WriteDoubleWord(busWordAddress - FLASH_MEM_BASE_ADDR, value);
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/DoProgramInfo: programFifo.Count = {0}",
                programFifo.Count);
        }

        private bool IsOperationAllowedInDefaultRegion(OperationType opType){
            bool ret = false;
            uint DEFAULT_REGION_VALUE = registersCollection.Read((long)Registers.DEFAULT_REGION);
            uint DEFAULT_REGION_READ_EN = 0x1 & DEFAULT_REGION_VALUE;
            uint DEFAULT_REGION_PROG_EN = (0x1 << 1) & DEFAULT_REGION_VALUE;
            uint DEFAULT_REGION_ERASE_EN = (0x1 << 2) & DEFAULT_REGION_VALUE;

            if(opType == OperationType.READ_DATA && DEFAULT_REGION_READ_EN != 0){
                ret = true;
            }else if(opType == OperationType.PROGRAM_DATA && DEFAULT_REGION_PROG_EN != 0){
                ret = true;
            }else if(opType == OperationType.ERASE_DATA_PAGE && DEFAULT_REGION_ERASE_EN != 0){
                ret = true;
            }

            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/IsOperationAllowedInDefaultRegion: ret = {0}",
                ret);

            if(!ret){
                this.Log(
                LogLevel.Info,
                    "OpenTitan_FlashController/IsOperationAllowedInDefaultRegion: " +
                    "Operation not allowed in the default region");
            }

            return ret;
        }

        private bool IsOperationAllowed(OperationType opType, long operationAddress){
            bool ret = IsOperationAllowedInDefaultRegion(opType);

            long[] protectionRegionRegisters = new long[] {
                (long)Registers.MP_REGION_CFG_0,
                (long)Registers.MP_REGION_CFG_1,
                (long)Registers.MP_REGION_CFG_2,
                (long)Registers.MP_REGION_CFG_3,
                (long)Registers.MP_REGION_CFG_4,
                (long)Registers.MP_REGION_CFG_5,
                (long)Registers.MP_REGION_CFG_6,
                (long)Registers.MP_REGION_CFG_7
            };

            foreach(long mpRegisterOffset in protectionRegionRegisters){
                if(MpRegionRegisterAppliesToOperation(mpRegisterOffset, operationAddress)){
                    ret = IsOperationAllowedInMpRegion(opType, mpRegisterOffset);
                }
            }

            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/IsOperationAllowed: ret = {0}",
                ret);

            if(!ret){
                this.Log(
                    LogLevel.Warning, "OpenTitan_FlashController/IsOperationAllowed: " +
                    "Operation not allowed!");
            }

            return ret;
        }

        private bool IsOperationAllowedInMpRegion(OperationType opType, long mpRegisterOffset){
            bool ret = false;

            uint MP_REGION_CFG_N_VALUE = registersCollection.Read(mpRegisterOffset);
            uint MP_REGION_CFG_N_RD_EN_FLAG = (0x1 << 1) & MP_REGION_CFG_N_VALUE;
            uint MP_REGION_CFG_N_PROG_EN_FLAG = (0x1 << 2) & MP_REGION_CFG_N_VALUE;
            uint MP_REGION_CFG_N_ERASE_EN_FLAG = (0x1 << 3) & MP_REGION_CFG_N_VALUE;

            if(opType == OperationType.READ_DATA){
                ret = MP_REGION_CFG_N_RD_EN_FLAG != 0;
            }else if(opType == OperationType.PROGRAM_DATA){
                ret = MP_REGION_CFG_N_PROG_EN_FLAG != 0;
            }else if(opType == OperationType.ERASE_DATA_PAGE){
                ret = MP_REGION_CFG_N_ERASE_EN_FLAG != 0;
            }
            this.Log(
                LogLevel.Noisy,
                "OpenTitan_FlashController/IsOperationAllowedInMpRegion: ret = {0}",
                ret);

            if(!ret){
                this.Log(
                    LogLevel.Info, "OpenTitan_FlashController/IsOperationAllowedInMpRegion: " +
                    "Operation not allowed!");
            }

            return ret;
        }

        private bool MpRegionRegisterAppliesToOperation(long mpRegisterOffset, long operationAddress){
            uint MP_REGION_CFG_N_VALUE = registersCollection.Read(mpRegisterOffset);
            uint MP_REGION_CFG_N_EN = MP_REGION_CFG_N_VALUE & 0x1;
            uint MP_REGION_CFG_N_BASE = (0x1FF) & (MP_REGION_CFG_N_VALUE >> 8);
            uint MP_REGION_CFG_N_SIZE = (0x3FF) & (MP_REGION_CFG_N_VALUE >> 20);

            long regionStart = FLASH_MEM_BASE_ADDR + (MP_REGION_CFG_N_BASE * FLASH_WORDS_PER_PAGE * FLASH_WORD_SZ);
            long regionEnd = regionStart + (MP_REGION_CFG_N_SIZE * FLASH_WORDS_PER_PAGE * FLASH_WORD_SZ);

            if(MP_REGION_CFG_N_EN != 0x0
                && operationAddress >= regionStart
                && operationAddress < regionEnd){ // the bus is 4 bytes wide. Should that effect the border logic?
                return true;
            }

            return false;
        }

        private const uint FLASH_WORDS_PER_PAGE = 128;
        private const uint FLASH_WORD_SZ = 8;
        private const uint FLASH_PAGES_PER_BANK = 256;
        private const long FLASH_MEM_BASE_ADDR = 0x20000000;
        private readonly DoubleWordRegisterCollection registersCollection;
        public long Size => 0x1000;
        private readonly MappedMemory flash;
        private MappedMemory infoFlash;
        private Queue<FlashControllerWorkItem> workQueue;
        private Queue<uint> readFifo;
        private uint readFifoDepth = 16;
        private Queue<uint> programFifo;
        private uint programFifoDepth = 16;
        private IFlagRegisterField opStatusRegisterDoneFlag;
        private IFlagRegisterField opStatusRegisterErrorFlag;
        private IFlagRegisterField interuptStatusRegisterOpDoneFlag;
        private IFlagRegisterField interruptStatusRegisterOpErrorFlag;
        private OperationState opState;

        bool supressFullProgramFifoWarning;
        private enum Registers : long
        {
            INTR_STATE = 0x00, // Reset default = 0x0, mask 0x1f
            INTR_ENABLE = 0x04, // Reset default = 0x0, mask 0x1f
            INTR_TEST = 0x08, // Reset default = 0x0, mask 0x1f
            CTRL_REGWEN = 0x0c, // Reset default = 0x1, mask 0x1
            CONTROL = 0x10, // Reset default = 0x0, mask 0xfff07f1
            ADDR = 0x14, // Reset default = 0x0, mask 0xffffffff
            REGION_CFG_REGWEN_0 = 0x18, // Reset default = 0x1, mask 0x1
            REGION_CFG_REGWEN_1 = 0x1c, // Reset default = 0x1, mask 0x1
            REGION_CFG_REGWEN_2 = 0x20, // Reset default = 0x1, mask 0x1
            REGION_CFG_REGWEN_3 = 0x24, // Reset default = 0x1, mask 0x1
            REGION_CFG_REGWEN_4 = 0x28, // Reset default = 0x1, mask 0x1
            REGION_CFG_REGWEN_5 = 0x2c, // Reset default = 0x1, mask 0x1
            REGION_CFG_REGWEN_6 = 0x30, // Reset default = 0x1, mask 0x1
            REGION_CFG_REGWEN_7 = 0x34, // Reset default = 0x1, mask 0x1
            MP_REGION_CFG_0 = 0x38, // Reset default = 0x0, mask 0x7ffff7f
            MP_REGION_CFG_1 = 0x3c, // Reset default = 0x0, mask 0x7ffff7f
            MP_REGION_CFG_2 = 0x40, // Reset default = 0x0, mask 0x7ffff7f
            MP_REGION_CFG_3 = 0x44, // Reset default = 0x0, mask 0x7ffff7f
            MP_REGION_CFG_4 = 0x48, // Reset default = 0x0, mask 0x7ffff7f
            MP_REGION_CFG_5 = 0x4c, // Reset default = 0x0, mask 0x7ffff7f
            MP_REGION_CFG_6 = 0x50, // Reset default = 0x0, mask 0x7ffff7f
            MP_REGION_CFG_7 = 0x54, // Reset default = 0x0, mask 0x7ffff7f
            BANK0_INFO0_REGWEN_0 = 0x58, // Reset default = 0x1, mask 0x1
            BANK0_INFO0_REGWEN_1 = 0x5c, // Reset default = 0x1, mask 0x1
            BANK0_INFO0_REGWEN_2 = 0x60, // Reset default = 0x1, mask 0x1
            BANK0_INFO0_REGWEN_3 = 0x64, // Reset default = 0x1, mask 0x1
            BANK0_INFO1_REGWEN_0 = 0x68, // Reset default = 0x1, mask 0x1
            BANK0_INFO1_REGWEN_1 = 0x6c, // Reset default = 0x1, mask 0x1
            BANK0_INFO1_REGWEN_2 = 0x70, // Reset default = 0x1, mask 0x1
            BANK0_INFO1_REGWEN_3 = 0x74, // Reset default = 0x1, mask 0x1
            BANK0_INFO0_PAGE_CFG_0 = 0x78, // Reset default = 0x0, mask 0x7f
            BANK0_INFO0_PAGE_CFG_1 = 0x7c, // Reset default = 0x0, mask 0x7f
            BANK0_INFO0_PAGE_CFG_2 = 0x80, // Reset default = 0x0, mask 0x7f
            BANK0_INFO0_PAGE_CFG_3 = 0x84, // Reset default = 0x0, mask 0x7f
            BANK0_INFO1_PAGE_CFG_0 = 0x88, // Reset default = 0x0, mask 0x7f
            BANK0_INFO1_PAGE_CFG_1 = 0x8c, // Reset default = 0x0, mask 0x7f
            BANK0_INFO1_PAGE_CFG_2 = 0x90, // Reset default = 0x0, mask 0x7f
            BANK0_INFO1_PAGE_CFG_3 = 0x94, // Reset default = 0x0, mask 0x7f
            BANK1_INFO0_REGWEN_0 = 0x98, // Reset default = 0x1, mask 0x1
            BANK1_INFO0_REGWEN_1 = 0x9c, // Reset default = 0x1, mask 0x1
            BANK1_INFO0_REGWEN_2 = 0xa0, // Reset default = 0x1, mask 0x1
            BANK1_INFO0_REGWEN_3 = 0xa4, // Reset default = 0x1, mask 0x1
            BANK1_INFO1_REGWEN_0 = 0xa8, // Reset default = 0x1, mask 0x1
            BANK1_INFO1_REGWEN_1 = 0xac, // Reset default = 0x1, mask 0x1
            BANK1_INFO1_REGWEN_2 = 0xb0, // Reset default = 0x1, mask 0x1
            BANK1_INFO1_REGWEN_3 = 0xb4, // Reset default = 0x1, mask 0x1
            BANK1_INFO0_PAGE_CFG_0 = 0xb8, // Reset default = 0x0, mask 0x7f
            BANK1_INFO0_PAGE_CFG_1 = 0xbc, // Reset default = 0x0, mask 0x7f
            BANK1_INFO0_PAGE_CFG_2 = 0xc0, // Reset default = 0x0, mask 0x7f
            BANK1_INFO0_PAGE_CFG_3 = 0xc4, // Reset default = 0x0, mask 0x7f
            BANK1_INFO1_PAGE_CFG_0 = 0xc8, // Reset default = 0x0, mask 0x7f
            BANK1_INFO1_PAGE_CFG_1 = 0xcc, // Reset default = 0x0, mask 0x7f
            BANK1_INFO1_PAGE_CFG_2 = 0xd0, // Reset default = 0x0, mask 0x7f
            BANK1_INFO1_PAGE_CFG_3 = 0xd4, // Reset default = 0x0, mask 0x7f
            DEFAULT_REGION = 0xd8, // Reset default = 0x0, mask 0x3f
            BANK_CFG_REGWEN = 0xdc, // Reset default = 0x1, mask 0x1
            MP_BANK_CFG = 0xe0, // Reset default = 0x0, mask 0x3
            OP_STATUS = 0xe4, // Reset default = 0x0, mask 0x3
            STATUS = 0xe8, // Reset default = 0xa, mask 0x1f
            PHY_STATUS = 0xec, // Reset default = 0x6, mask 0x7
            Scratch = 0xf0, // Reset default = 0x0, mask 0xffffffff
            FIFO_LVL = 0xf4, // Reset default = 0xf0f, mask 0x1f1f
            FIFO_RST = 0xf8, // Reset default = 0x0, mask 0x1
            prog_fifo = 0xfc, // 1 item wo window. Byte writes are not supported
            rd_fifo = 0x100, // 1 item ro window. Byte writes are not supported
        }
    } // class

    public class FlashControllerWorkItem{
        public FlashControllerWorkItem(){}

        public FlashControllerWorkItem WithBankErase(uint bankNumber){
            eraseBank = true;
            eraseBankNumber = bankNumber;
            return this;
        }

        public FlashControllerWorkItem WithDataPageErase(long addr){
            eraseDataPage = true;
            erasePageAddress = addr;
            return this;
        }

        public FlashControllerWorkItem WithInfoPageErase(long addr){
            eraseInfoPage = true;
            erasePageAddress = addr;
            return this;
        }

        public FlashControllerWorkItem WithDataRead(long addr){
            readData = true;
            readAddress = addr;
            return this;
        }

        public FlashControllerWorkItem WithInfoRead(long addr){
            readInfo = true;
            readAddress = addr;
            return this;
        }

        public FlashControllerWorkItem WithDataProgram(long addr){
            programData = true;
            programAddress = addr;
            return this;
        }

        public FlashControllerWorkItem WithInfoProgram(long addr){
            programInfo = true;
            programAddress = addr;
            return this;
        }

        public bool eraseBank;
        public uint eraseBankNumber;

        public bool eraseDataPage;
        public bool eraseInfoPage;
        public long erasePageAddress;

        public bool readData;
        public bool readInfo;
        public long readAddress;

        public bool programData;
        public bool programInfo;
        public long programAddress;
    } // class
} // namespace
