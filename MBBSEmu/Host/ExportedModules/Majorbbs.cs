﻿using MBBSEmu.CPU;
using MBBSEmu.Logging;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MBBSEmu.Module;

namespace MBBSEmu.Host
{
    /// <summary>
    ///     Class which defines functions that are part of the MajorBBS/WG SDK and included in
    ///     MAJORBBS.H.
    ///
    ///     While a majority of these functions are specific to MajorBBS/WG, some are just proxies for
    ///     Borland C++ macros and are noted as such.
    /// </summary>
    public class Majorbbs
    {
        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger(typeof(CustomLogger));

        private readonly MbbsHostMemory _mbbsHostMemory;
        private readonly CpuCore _cpu;
        private readonly MbbsModule _module;

        /// <summary>
        ///     Imported Functions from the REGISTER_MODULE method are saved here.
        ///
        ///     Tuple is INT16:INT16 (SEGMENT:OFFSET) of method entry
        /// </summary>
        public readonly Dictionary<string, Tuple<int, int>> ModuleRoutines;

        public readonly Dictionary<int, MsgFile> MsgFiles;

        public Majorbbs(CpuCore cpuCore,  MbbsModule module)
        {
            _mbbsHostMemory = new MbbsHostMemory();
            _cpu = cpuCore;
            _module = module;
            ModuleRoutines = new Dictionary<string, Tuple<int, int>>();
            MsgFiles = new Dictionary<int, MsgFile>();
        }

        /// <summary>
        ///     Initializes the Pseudo-Random Number Generator with the given seen
        ///
        ///     Since we'll handle this internally, we'll just ignore this
        ///
        ///     Signature: void srand (unsigned int seed);
        /// </summary>
        [ExportedModuleFunction(Name = "SRAND", Ordinal = 561)]
        public void srand()
        {
            //Pop the input int, since we're ignoring this
            _cpu.Memory.Pop(_cpu.Registers.SP);
            _cpu.Registers.SP += 2;
        }

        /// <summary>
        ///     Get the current calendar time as a value of type time_t
        ///     Epoch Time
        /// 
        ///     Signature: time_t time (time_t* timer);
        ///     Return: Value is 32-Bit TIME_T (AX:DX)
        /// </summary>
        [ExportedModuleFunction(Name = "TIME", Ordinal = 599)]
        public void time()
        {
            //For now, ignore the input pointer for time_t
            var input1 = _cpu.Memory.Pop(_cpu.Registers.BP + 4);
            var input2 = _cpu.Memory.Pop(_cpu.Registers.BP + 6);

            var outputArray = new byte[4];
            var passedSeconds = (int)(DateTime.Now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            Array.Copy(BitConverter.GetBytes(passedSeconds), 0, outputArray, 0, 4);

            _cpu.Registers.AX = BitConverter.ToUInt16(outputArray, 2);
            _cpu.Registers.DX = BitConverter.ToUInt16(outputArray, 0);

#if DEBUG
            _logger.Info($"time() passed seconds: {passedSeconds} (AX:{_cpu.Registers.AX:X4}, DX:{_cpu.Registers.DX:X4})");
#endif
        }



        /// <summary>
        ///     Allocate a new memory block and zeros it out
        /// 
        ///     Signature: char *alczer(unsigned nbytes);
        ///     Return: AX = Offset in Segment (host)
        ///             DX = Data Segment
        /// </summary>
        [ExportedModuleFunction(Name = "ALCZER", Ordinal = 68)]
        public void alczer()
        {
            var size = _cpu.Memory.Pop(_cpu.Registers.BP + 4);

            //Get the current pointer
            var pointer = _mbbsHostMemory.AllocateHostMemory(size);

            _cpu.Registers.AX = pointer;
            _cpu.Registers.DX = 0xFFFF;

#if DEBUG
            _logger.Info($"alczer() allocated {size} bytes starting at {pointer:X4}");
#endif
        }

        /// <summary>
        ///     Get's a module's name from the specified .MDF file
        /// 
        ///     Signature: char *gmdnam(char *mdfnam);
        ///     Return: AX = Offset in Segment
        ///             DX = Data Segment
        /// </summary>
        [ExportedModuleFunction(Name = "GMDNAM", Ordinal = 331)]
        public void gmdnam()
        {
            var datSegmentOffset = _cpu.Memory.Pop(_cpu.Registers.BP + 4);
            var dataSegment = _cpu.Memory.Pop(_cpu.Registers.BP + 6);
            var size = _cpu.Memory.Pop(_cpu.Registers.BP + 8);

            //Get the current pointer
            var pointer = _mbbsHostMemory.AllocateHostMemory(size);

            //Get the Module Name from the Mdf
            var moduleName = _module.Mdf.ModuleName;

            //Sanity Check -- 
            if (moduleName.Length > size)
            {
                _logger.Warn($"Module Name \"{moduleName}\" greater than specified size {size}, truncating");
                moduleName = moduleName.Substring(0, size);
            }

            _mbbsHostMemory.SetHostArray(pointer, Encoding.ASCII.GetBytes(moduleName));

            _cpu.Registers.AX = pointer;
            _cpu.Registers.DX = 0xFFFF;

#if DEBUG
            _logger.Info($"gmdnam() retrieved module name \"{moduleName}\" and saved it at host memory offset {pointer:X4}");
#endif
        }

        /// <summary>
        ///     Copies the source string to the destination with a limit
        ///
        ///     Signature: stzcpy(char *dest, char *source, unsigned nbytes);
        ///     Return: AX = Offset in Segment
        ///             DX = Data Segment
        /// </summary>
        [ExportedModuleFunction(Name = "STZCPY", Ordinal = 589)]
        public void stzcpy()
        {
            var destinationOffset = _cpu.Memory.Pop(_cpu.Registers.BP + 4);
            var destinationSegment = _cpu.Memory.Pop(_cpu.Registers.BP + 6);
            var srcOffset = _cpu.Memory.Pop(_cpu.Registers.BP + 8);
            var srcSegment = _cpu.Memory.Pop(_cpu.Registers.BP + 10);

            var inputBuffer = new List<byte>();
            int bytesCopied = 0;
            if (srcSegment == 0xFFFF)
            {
                for (var i = 0; i < ushort.MaxValue; i++)
                {
                    bytesCopied++;
                    var inputByte = (byte)_mbbsHostMemory.GetHostByte(srcOffset + i);
                    inputBuffer.Add(inputByte);
                    if (inputByte == 0)
                        break;
                }
            }
            else
            {
                inputBuffer.AddRange(_cpu.Memory.GetString(srcSegment, srcOffset));
            }

            if (destinationSegment == 0xFFFF)
            {
                _mbbsHostMemory.SetHostArray(destinationOffset, inputBuffer.ToArray());
            }
            else
            {
                _cpu.Memory.SetArray(destinationSegment, destinationOffset, inputBuffer.ToArray());
            }

#if DEBUG
            _logger.Info($"stzcpy() copied {bytesCopied} bytes from {srcSegment:X4}:{srcOffset:X4} to {destinationSegment:X4}:{destinationOffset:X4}");
#endif

            _cpu.Registers.AX = destinationOffset;
            _cpu.Registers.DX = destinationSegment;
        }

        /// <summary>
        ///     Registers the Module with the MajorBBS system
        ///
        ///     Signature: int register_module(struct module *mod)
        ///     Return: AX = Value of usrptr->state whenever user is 'in' this module
        /// </summary>
        [ExportedModuleFunction(Name = "REGISTER_MODULE", Ordinal = 492)]
        public void register_module()
        {
            var destinationOffset = _cpu.Memory.Pop(_cpu.Registers.BP + 4);
            var destinationSegment = _cpu.Memory.Pop(_cpu.Registers.BP + 6);

            var moduleStruct = _cpu.Memory.GetArray(destinationSegment, destinationOffset, 61);

            var relocationRecords =
                _module.File.SegmentTable.First(x => x.Ordinal == destinationSegment).RelocationRecords;

            //Description for Main Menu
            var moduleDescription = Encoding.ASCII.GetString(moduleStruct, 0, 25).Trim();
#if DEBUG
            _logger.Info($"Module Description set to {moduleDescription}");
#endif

            var moduleRoutines = new[]
                {"lonrou", "sttrou", "stsrou", "injrou", "lofrou", "huprou", "mcurou", "dlarou", "finrou"};

            for (var i = 0; i < 9; i++)
            {
                var currentOffset = 25 + (i * 4);
                var routineEntryPoint = new byte[4];
                Array.Copy(moduleStruct, currentOffset, routineEntryPoint, 0, 4);

                //If there's a Relocation record for this routine, apply it
                if (relocationRecords.Any(y => y.Offset == currentOffset))
                {
                    var routineRelocationRecord = relocationRecords.First(x => x.Offset == currentOffset);
                    Array.Copy(BitConverter.GetBytes(routineRelocationRecord.TargetTypeValueTuple.Item4), 0, routineEntryPoint, 0, 2);
                    Array.Copy(BitConverter.GetBytes(routineRelocationRecord.TargetTypeValueTuple.Item2), 0, routineEntryPoint, 2, 2);
                }
#if DEBUG
                _logger.Info($"Routine {moduleRoutines[i]} set to {BitConverter.ToUInt16(routineEntryPoint, 2):X4}:{BitConverter.ToUInt16(routineEntryPoint, 0):X4}");
#endif
            }

            //usrptr->state is the Module Number in use, as assigned by the host process
            //Because we only support 1 module running at a time right now, we just set this to one
            _cpu.Registers.AX = 1;
        }

        /// <summary>
        ///     Opens the specified CNF file (.MCV in runtime form)
        ///
        ///     Signature: FILE *mbkprt=opnmsg(char *fileName)
        ///     Return: AX = Offset in Segment
        ///             DX = Host Segment
        /// </summary>
        [ExportedModuleFunction(Name = "OPNMSG", Ordinal = 456)]
        public void opnmsg()
        {
            var sourceOffset = _cpu.Memory.Pop(_cpu.Registers.BP + 4);
            var sourceSegment = _cpu.Memory.Pop(_cpu.Registers.BP + 6);

            var msgFileName = sourceSegment <= 0xFF
                ? Encoding.ASCII.GetString(_cpu.Memory.GetString(sourceSegment, sourceOffset))
                : Encoding.ASCII.GetString(_mbbsHostMemory.GetString(sourceSegment, sourceOffset));

            msgFileName = msgFileName.TrimEnd('\0');

            if(_module.Msg.FileNameAtRuntime != msgFileName)
                throw new FileNotFoundException($"Module attempting to load unknown MSG file: {msgFileName}");

            MsgFiles.Add(1, _module.Msg);

#if DEBUG
            _logger.Info($"opnmsg() opened MSG file: {msgFileName}, assigned to {(int)EnumHostSegments.MsgPointer:X4}:1");
#endif

            _cpu.Registers.AX = 1;
            _cpu.Registers.DX = (int)EnumHostSegments.MsgPointer;
        }

        /// <summary>
        ///     Retrieves a numeric option from MCV file
        ///
        ///     Signature: int numopt(int msgnum,int floor,int ceiling)
        ///     Return: AX = Value retrieved
        /// </summary>
        [ExportedModuleFunction(Name = "NUMOPT", Ordinal = 441)]
        public void numopt()
        {
            if(MsgFiles.Count == 0)
                throw new Exception("Attempted to read configuration value from MSG file prior to calling opnmsg()");

            var msgnum = _cpu.Memory.Pop(_cpu.Registers.BP + 4);
            var floor = _cpu.Memory.Pop(_cpu.Registers.BP + 6);
            var ceiling = _cpu.Memory.Pop(_cpu.Registers.BP + 8);

            var msgRecord = MsgFiles[1].MsgRecords.First(x => x.Ordinal == msgnum);

            var outputValue = string.IsNullOrEmpty(msgRecord.Value)
                ? int.Parse(msgRecord.DefaultValue)
                : int.Parse(msgRecord.Value);

            //Validate
            if(outputValue < floor || outputValue >  ceiling)
                throw new ArgumentOutOfRangeException($"{msgnum} ({msgRecord.Name}) value {outputValue} is outside specified bounds");

#if DEBUG
            _logger.Info($"numopt() retrieved option {msgnum} ({msgRecord.Name}) value: {outputValue}");
#endif

            _cpu.Registers.AX = outputValue;
        }

        /// <summary>
        ///     Retrieves a yes/no option from an MCV file
        ///
        ///     Signature: int ynopt(int msgnum)
        ///     Return: AX = 1/Yes, 0/No
        /// </summary>
        [ExportedModuleFunction(Name = "YNOPT", Ordinal = 650)]
        public void ynopt()
        {
            var msgnum = _cpu.Memory.Pop(_cpu.Registers.BP + 4);

            var msgRecord = MsgFiles[1].MsgRecords.First(x => x.Ordinal == msgnum);

            var outputValue = string.IsNullOrEmpty(msgRecord.Value)
                ? msgRecord.DefaultValue.StartsWith('Y')
                : msgRecord.Value.StartsWith('Y');

#if DEBUG
            _logger.Info($"ynopt() retrieved option {msgnum} ({msgRecord.Name}) value: {outputValue}");
#endif

            _cpu.Registers.AX = outputValue ? 1 : 0;
        }

        /// <summary>
        ///     Gets a long (32-bit) numeric option from the MCV File
        ///
        ///     Signature: long lngopt(int msgnum,long floor,long ceiling)
        ///     Return: AX = Most Significant 16-Bits
        ///             DX = Least Significant 16-Bits
        /// </summary>
        [ExportedModuleFunction(Name = "LNGOPT", Ordinal = 389)]
        public void lngopt()
        {
            var msgnum =  _cpu.Memory.Pop(_cpu.Registers.BP + 4);

            var floorLow = _cpu.Memory.Pop(_cpu.Registers.BP + 6);
            var floorHigh = _cpu.Memory.Pop(_cpu.Registers.BP + 8);

            var ceilingLow = _cpu.Memory.Pop(_cpu.Registers.BP + 10);
            var ceilingHigh = _cpu.Memory.Pop(_cpu.Registers.BP + 12);

            var floor = floorHigh << 16 | floorLow;
            var ceiling = ceilingHigh << 16 | ceilingLow;

            var msgRecord = MsgFiles[1].MsgRecords.First(x => x.Ordinal == msgnum);

            var outputValue = string.IsNullOrEmpty(msgRecord.Value)
                ? int.Parse(msgRecord.DefaultValue)
                : int.Parse(msgRecord.Value);

            //Validate
            if (outputValue < floor || outputValue > ceiling)
                throw new ArgumentOutOfRangeException($"{msgnum} ({msgRecord.Name}) value {outputValue} is outside specified bounds");

#if DEBUG
            _logger.Info($"lngopt() retrieved option {msgnum} ({msgRecord.Name}) value: {outputValue}");
#endif

            _cpu.Registers.AX = (int) (outputValue & 0xFFFF0000);
            _cpu.Registers.DX = outputValue & 0xFFFF;
        }

        /// <summary>
        ///     Gets a string from an MCV file
        ///
        ///     Signature: char *string=stgopt(int msgnum)
        ///     Return: AX = Offset in Segment
        ///             DX = Host Segment     
        /// </summary>
        [ExportedModuleFunction(Name = "STGOPT", Ordinal = 566)]
        public void stgopt()
        {
            var msgnum = _cpu.Memory.Pop(_cpu.Registers.BP + 4);

            var msgRecord = MsgFiles[1].MsgRecords.First(x => x.Ordinal == msgnum);

            var outputValue = string.IsNullOrEmpty(msgRecord.Value)
                ? msgRecord.DefaultValue
                : msgRecord.Value;

            //Make the string null terminated
            outputValue += '\0';

            var outputValueOffset = _mbbsHostMemory.AllocateHostMemory(outputValue.Length);
            _mbbsHostMemory.SetHostArray(outputValueOffset, Encoding.ASCII.GetBytes(outputValue));

#if DEBUG
            _logger.Info($"stgopt() retrieved option {msgnum} ({msgRecord.Name}) value: {outputValue} saved to {(int)EnumHostSegments.MemoryPointer}:{outputValueOffset}");
#endif

            _cpu.Registers.AX = outputValueOffset;
            _cpu.Registers.DX = (int)EnumHostSegments.MemoryPointer;
        }

        /// <summary>
        ///     Read value of CNF option (text blocks with ASCII compatible line terminators)
        ///
        ///     Functionally, as far as this helper method is concerned, there's no difference between this method and stgopt()
        /// 
        ///     Signature: char *bufadr=getasc(int msgnum)
        ///     Return: AX = Offset in Segment
        ///             DX = Host Segment 
        /// </summary>
        [ExportedModuleFunction(Name = "GETASC", Ordinal = 316)]
        public void getasc()
        {
#if DEBUG
            _logger.Info($"getasc() called, redirecting to stgopt()");
#endif
            stgopt();
        } 
    }
}
