﻿using MBBSEmu.Btrieve;
using MBBSEmu.CPU;
using MBBSEmu.HostProcess.Structs;
using MBBSEmu.Memory;
using MBBSEmu.Module;
using MBBSEmu.Session;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;

namespace MBBSEmu.HostProcess.ExportedModules
{
    /// <summary>
    ///     Class which defines functions that are part of the MajorBBS/WG SDK and included in
    ///     MAJORBBS.H.
    ///
    ///     While a majority of these functions are specific to MajorBBS/WG, some are just proxies for
    ///     Borland C++ macros and are noted as such.
    /// </summary>
    public class Majorbbs : ExportedModuleBase, IExportedModule
    {
        public IntPtr16 GlobalCommandHandler;

        private IntPtr16 _currentMcvFile;
        private readonly Queue<IntPtr16> _previousMcvFile;

        private BtrieveFile _currentBtrieveFile;
        private BtrieveFile _previousBtrieveFile;

        private readonly Dictionary<byte[], IntPtr16> _textVariables = new Dictionary<byte[], IntPtr16>();

        private ushort _channelNumber;

        private readonly List<IntPtr16> _margvPointers;
        private readonly List<IntPtr16> _margnPointers;
        private int _inputCurrentCommand;

        private int _outputBufferPosition;

        public Majorbbs(MbbsModule module, PointerDictionary<UserSession> channelDictionary) : base(module,
            channelDictionary)
        {
            _outputBufferPosition = 0;
            _margvPointers = new List<IntPtr16>();
            _margnPointers = new List<IntPtr16>();
            _inputCurrentCommand = 0;
            _previousMcvFile = new Queue<IntPtr16>(10);
            //Setup Memory Spaces
            Module.Memory.AllocateVariable("PRFBUF", 0x2000); //Output buffer, 8kb
            Module.Memory.AllocateVariable("PRFPTR", 0x4);
            Module.Memory.AllocateVariable("INPUT", 0xFF); //256 Byte Maximum user Input
            Module.Memory.AllocateVariable("USER", 0x28D7); //41 bytes * 255 users
            Module.Memory.AllocateVariable("USRPTR", 0x4); //pointer to the current USER record

            Module.Memory.AllocateVariable("STATUS", 0x2); //ushort Status
            Module.Memory.AllocateVariable("CHANNEL", 0x1FE); //255 channels * 2 bytes
            Module.Memory.AllocateVariable("MARGC", 0x2);
            Module.Memory.AllocateVariable("MARGN", 0x3FC);
            Module.Memory.AllocateVariable("MARGV", 0x3FC);
            Module.Memory.AllocateVariable("INPLEN", 0x2);
            Module.Memory.AllocateVariable("USERNUM", 0x2);
            var usraccPointer = Module.Memory.AllocateVariable("USRACC", 0x155);
            var usaptrPointer = Module.Memory.AllocateVariable("USAPTR", 0x4);
            Module.Memory.SetArray(usaptrPointer, usraccPointer.ToSpan());
            Module.Memory.AllocateVariable("VDAPTR", 0x4);
            var ntermsPointer = Module.Memory.AllocateVariable("NTERMS", 0x2); //ushort number of lines
            Module.Memory.SetWord(ntermsPointer, 0x04); //4 channels for now
            Module.Memory.AllocateVariable("GENBB", 0x4); //Pointer to GENBB BTRIEVE File
            Module.Memory.AllocateVariable("OTHUSN", 0x2); //Set by onsys() or instat()
        }

        /// <summary>
        ///     Sets State for the Module Registers and the current Channel Number that's running
        /// </summary>
        /// <param name="registers"></param>
        /// <param name="channelNumber"></param>
        public void SetState(CpuRegisters registers, ushort channelNumber)
        {
            Registers = registers;
            _channelNumber = channelNumber;

            //Bail if it's max value, not processing any input or status
            if (channelNumber == ushort.MaxValue)
                return;

            Module.Memory.SetWord(Module.Memory.GetVariable("USERNUM"), channelNumber);
            Module.Memory.SetWord(Module.Memory.GetVariable("STATUS"), ChannelDictionary[channelNumber].Status);
            var pointer = Module.Memory.GetVariable("USER");
            pointer.Offset += (ushort)(_channelNumber * 41);
            Module.Memory.SetArray(pointer, ChannelDictionary[channelNumber].UsrPtr.ToSpan());
            Module.Memory.SetArray(Module.Memory.GetVariable("USRACC"), ChannelDictionary[channelNumber].UsrAcc.ToSpan());

            //Write Blank Input
            var inputMemory = Module.Memory.GetVariable("INPUT");
            Module.Memory.SetByte(inputMemory.Segment, inputMemory.Offset, 0x0);

            //Processing Channel Input
            if (ChannelDictionary[channelNumber].Status == 3)
            {
                ChannelDictionary[channelNumber].InputBuffer.WriteByte(0x0);
                ChannelDictionary[channelNumber].parsin();
                ChannelDictionary[channelNumber].InputBuffer.SetLength(0);
                Module.Memory.SetWord(Module.Memory.GetVariable("MARGC"), (ushort)ChannelDictionary[channelNumber].mArgCount);
                Module.Memory.SetWord(Module.Memory.GetVariable("INPLEN"), (ushort)ChannelDictionary[channelNumber].InputCommand.Length);
                Module.Memory.SetArray(inputMemory.Segment, inputMemory.Offset, ChannelDictionary[channelNumber].InputCommand);
                _logger.Info($"Input Length {ChannelDictionary[channelNumber].InputCommand.Length} written to {inputMemory.Segment:X4}:{inputMemory.Offset}");
                var margnPointer = Module.Memory.GetVariable("MARGN");
                var margvPointer = Module.Memory.GetVariable("MARGV");

                //Build Command Word Pointers
                for (var i = 0; i < ChannelDictionary[channelNumber].mArgCount; i++)
                {
                    var currentMargnPointer = new IntPtr16(inputMemory.Segment,
                        (ushort)(inputMemory.Offset + ChannelDictionary[channelNumber].mArgn[i]));
                    var currentMargVPointer = new IntPtr16(inputMemory.Segment,
                        (ushort)(inputMemory.Offset + ChannelDictionary[channelNumber].mArgv[i]));

                    _logger.Info($"Command {i} {currentMargVPointer.Segment:X4}:{currentMargVPointer.Offset:X4}-{currentMargnPointer.Segment:X4}:{currentMargnPointer.Offset:X4}");

                    Module.Memory.SetArray(margnPointer.Segment, (ushort)(margnPointer.Offset + (i * 4)),
                        currentMargnPointer.ToSpan());

                    Module.Memory.SetArray(margvPointer.Segment, (ushort)(margvPointer.Offset + (i * 4)),
                        currentMargVPointer.ToSpan());
                }
            }
        }

        /// <summary>
        ///     Updates the Session with any information from memory before execution finishes
        /// </summary>
        /// <param name="channel"></param>
        public void UpdateSession(ushort channel)
        {
            //If the status wasn't set internally, then set it to the default 5
            ChannelDictionary[_channelNumber].Status = !ChannelDictionary[_channelNumber].StatusChange
                ? (ushort)5
                : Module.Memory.GetWord(base.Module.Memory.GetVariable("STATUS"));

            var userPointer = Module.Memory.GetVariable("USER");
            ChannelDictionary[_channelNumber].UsrPtr.FromSpan(Module.Memory.GetArray(userPointer.Segment,
                (ushort)(userPointer.Offset + (channel * 41)), 41));
        }

        /// <summary>
        ///     Invokes method by the specified ordinal
        /// </summary>
        /// <param name="ordinal"></param>
        /// <param name="offsetsOnly"></param>
        public ReadOnlySpan<byte> Invoke(ushort ordinal, bool offsetsOnly = false)
        {
            switch (ordinal)
            {
                case 628:
                    return usernum;
                case 629:
                    return usrptr;
                case 97:
                    return channel;
                case 565:
                    return status;
                case 475:
                    return prfbuf;
                case 437:
                    return nterms;
                case 625:
                    return user;
                case 637:
                    return vdaptr;
                case 401:
                    return margc;
                case 442:
                    return nxtcmd;
                case 477:
                    return prfptr;
                case 350:
                    return input;
                case 349:
                    return inplen;
                case 402:
                    return margn;
                case 403:
                    return margv;
                case 16:
                    return _exitbuf;
                case 17:
                    return _exitfopen;
                case 18:
                    return _exitopen;
                case 624:
                    return usaptr;
                case 757:
                    return genbb;
                case 459:
                    return othusn;
            }

            if (offsetsOnly)
            {
                var methodPointer = new IntPtr16(0xFFFF, ordinal);
#if DEBUG
                //_logger.Info($"Returning Method Offset {methodPointer.Segment:X4}:{methodPointer.Offset:X4}");
#endif
                return methodPointer.ToSpan();
            }

            switch (ordinal)
            {
                case 561:
                    srand();
                    break;
                case 599:
                    time();
                    break;
                case 68:
                    alczer();
                    break;
                case 331:
                    gmdnam();
                    break;
                case 574:
                    strcpy();
                    break;
                case 589:
                    stzcpy();
                    break;
                case 492:
                    register_module();
                    break;
                case 456:
                    opnmsg();
                    break;
                case 441:
                    numopt();
                    break;
                case 650:
                    ynopt();
                    break;
                case 389:
                    lngopt();
                    break;
                case 566:
                    stgopt();
                    break;
                case 316:
                    getasc();
                    break;
                case 377:
                    l2as();
                    break;
                case 77:
                    atol();
                    break;
                case 601:
                    today();
                    break;
                case 665:
                    f_scopy();
                    break;
                case 520:
                    sameas();
                    break;
                case 713:
                    uacoff();
                    break;
                case 474:
                    prf();
                    break;
                case 463:
                    outprf();
                    break;
                case 160:
                    dedcrd();
                    break;
                case 476:
                    prfmsg();
                    break;
                case 550:
                    shocst();
                    break;
                case 59:
                    addcrd();
                    break;
                case 366:
                    itoa();
                    break;
                case 334:
                    haskey();
                    break;
                case 335:
                    hasmkey();
                    break;
                case 486:
                    rand();
                    break;
                case 428:
                    ncdate();
                    break;
                case 167:
                    dfsthn();
                    break;
                case 119:
                    clsmsg();
                    break;
                case 515:
                    rtihdlr();
                    break;
                case 516:
                    rtkick();
                    break;
                case 543:
                    setmbk();
                    break;
                case 510:
                    rstmbk();
                    break;
                case 455:
                    opnbtv();
                    break;
                case 534:
                    setbtv();
                    break;
                case 569:
                    stpbtv();
                    break;
                case 505:
                    rstbtv();
                    break;
                case 621:
                    updbtv();
                    break;
                case 351:
                    insbtv();
                    break;
                case 488:
                    rdedcrd();
                    break;
                case 559:
                    spr();
                    break;
                case 659:
                    f_lxmul();
                    break;
                case 654:
                    f_ldiv();
                    break;
                case 113:
                    clrprf();
                    break;
                case 65:
                    alcmem();
                    break;
                case 643:
                    vsprintf();
                    break;
                case 1189:
                    scnmdf();
                    break;
                case 435:
                    now();
                    break;
                case 657:
                    f_lumod();
                    break;
                case 544:
                    setmem();
                    break;
                case 582:
                    strncpy();
                    break;
                case 494:
                    register_textvar();
                    break;
                case 997:
                    obtbtvl();
                    break;
                case 158:
                    dclvda();
                    break;
                case 636:
                    vdaoff();
                    break;
                case 87:
                    bgncnc();
                    break;
                case 522:
                    sameto();
                    break;
                case 122:
                    cncchr();
                    break;
                case 225:
                    f_open();
                    break;
                case 205:
                    f_close();
                    break;
                case 560:
                    sprintf();
                    break;
                case 694:
                    fnd1st();
                    break;
                case 229:
                    f_read();
                    break;
                case 312:
                    f_write();
                    break;
                case 617:
                    unlink();
                    break;
                case 578:
                    strlen();
                    break;
                case 603:
                    tolower();
                    break;
                case 604:
                    toupper();
                    break;
                case 496:
                    rename();
                    break;
                case 511:
                    rstrin();
                    break;
                case 580:
                    strncat();
                    break;
                case 411:
                    memset();
                    break;
                case 326:
                    getmsg();
                    break;
                case 562:
                    sscanf();
                    break;
                case 355:
                    intdos();
                    break;
                case 786:
                    prfmlt();
                    break;
                case 783:
                    clrmlt();
                    break;
                case 330:
                    globalcmd();
                    break;
                case 94:
                    catastro();
                    break;
                case 210:
                    fgets();
                    break;
                case 571:
                    strcat();
                    break;
                case 226:
                    f_printf();
                    break;
                case 266:
                    fseek();
                    break;
                case 430:
                    nctime();
                    break;
                case 572:
                    strchr();
                    break;
                case 467:
                    parsin();
                    break;
                case 420:
                    movemem();
                    break;
                case 267:
                    ftell();
                    break;
                case 352:
                    instat();
                    break;
                case 521:
                    samein();
                    break;
                case 553:
                    skpwht();
                    break;
                case 405:
                    mdfgets();
                    break;
                case 181:
                    echon();
                    break;
                case 587:
                    strupr();
                    break;
                case 584:
                    strstr();
                    break;
                case 164:
                    depad();
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown Exported Function Ordinal: {ordinal}");
            }

            return null;
        }

        /// <summary>
        ///     Initializes the Pseudo-Random Number Generator with the given seen
        ///
        ///     Since we'll handle this internally, we'll just ignore this
        ///
        ///     Signature: void srand (unsigned int seed);
        /// </summary>
        private void srand() { }

        /// <summary>
        ///     Get the current calendar time as a value of type time_t
        ///     Epoch Time
        /// 
        ///     Signature: time_t time (time_t* timer);
        ///     Return: Value is 32-Bit TIME_T (AX:DX)
        /// </summary>
        private void time()
        {
            //For now, ignore the input pointer for time_t

            var outputArray = new byte[4];
            var passedSeconds = (int)(DateTime.Now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            Array.Copy(BitConverter.GetBytes(passedSeconds), 0, outputArray, 0, 4);

            Registers.AX = BitConverter.ToUInt16(outputArray, 2);
            Registers.DX = BitConverter.ToUInt16(outputArray, 0);

#if DEBUG
            _logger.Info($"Passed seconds: {passedSeconds} (AX:{Registers.AX:X4}, DX:{Registers.DX:X4})");
#endif
        }

        /// <summary>
        ///     Allocate a new memory block and zeros it out on the host
        /// 
        ///     Signature: char *alczer(unsigned nbytes);
        ///     Return: AX = Offset in Segment (host)
        ///             DX = Data Segment
        /// </summary>
        private void alczer()
        {
            var size = GetParameter(0);

            var memoryPointer = Module.Memory.AllocateVariable(null, 4);

            //Get the current pointer
            var allocatedMemory = base.Module.Memory.AllocateVariable(null, size);

            //Write address of the memory segment to the pointer
            Module.Memory.SetArray(memoryPointer, allocatedMemory.ToSpan());

            Registers.AX = memoryPointer.Offset;
            Registers.DX = memoryPointer.Segment;

#if DEBUG
            _logger.Info($"Allocated {size} bytes starting at {allocatedMemory} (Pointer {memoryPointer})");
#endif
        }

        /// <summary>
        ///     Allocates Memory
        ///
        ///     Functionally, for our purposes, the same as alczer
        /// </summary>
        /// <returns></returns>
        private void alcmem() => alczer();

        /// <summary>
        ///     Get's a module's name from the specified .MDF file
        /// 
        ///     Signature: char *gmdnam(char *mdfnam);
        ///     Return: AX = Offset in Segment
        ///             DX = Data Segment
        /// </summary>
        private void gmdnam()
        {
            var dataSegmentPointer = GetParameterPointer(0);
            var size = GetParameter(2);

            //Only needs to be set once
            if (!Module.Memory.TryGetVariable("GMDNAM", out var variablePointer))
            {

                //Get the current pointer
                variablePointer = Module.Memory.AllocateVariable("GMDNAM", size);

                //Get the Module Name from the Mdf
                var moduleName = Module.Mdf.ModuleName;

                //Sanity Check -- 
                if (moduleName.Length > size)
                {
                    _logger.Warn($"Module Name \"{moduleName}\" greater than specified size {size}, truncating");
                    moduleName = moduleName.Substring(0, size);
                }

                //Set Memory
                Module.Memory.SetArray(variablePointer,
                    Encoding.Default.GetBytes(Module.Mdf.ModuleName));
#if DEBUG
                _logger.Info(
                    $"Retrieved Module Name \"{Module.Mdf.ModuleName}\" and saved it at host memory offset {Registers.DX:X4}:{Registers.AX:X4}");
#endif
            }

            Registers.AX = variablePointer.Offset;
            Registers.DX = variablePointer.Segment;
        }

        /// <summary>
        ///     Copies the C string pointed by source into the array pointed by destination, including the terminating null character
        ///
        ///     Signature: char* strcpy(char* destination, const char* source );
        ///     Return: AX = Offset in Segment
        ///             DX = Data Segment
        /// </summary>
        private void strcpy()
        {
            var destinationPointer = GetParameterPointer(0);
            var sourcePointer = GetParameterPointer(2);

            var inputBuffer = Module.Memory.GetString(sourcePointer);
            Module.Memory.SetArray(destinationPointer, inputBuffer);

#if DEBUG
            _logger.Info($"Copied {inputBuffer.Length} bytes from {sourcePointer} to {destinationPointer}");
#endif

            Registers.AX = destinationPointer.Offset;
            Registers.DX = destinationPointer.Segment;
        }

        /// <summary>
        ///     Copies a string with a fixed length
        ///
        ///     Signature: stzcpy(char *dest, char *source, int nbytes);
        ///     Return: AX = Offset in Segment
        ///             DX = Data Segment
        /// </summary>
        private void stzcpy()
        {
            var destinationPointer = GetParameterPointer(0);
            var sourcePointer = GetParameterPointer(2);
            var limit = GetParameter(4);

            using var inputBuffer = new MemoryStream();
            inputBuffer.Write(Module.Memory.GetArray(sourcePointer, limit));

            //If the value read is less than the limit, it'll be padded with null characters
            //per the MajorBBS Development Guide
            for (var i = inputBuffer.Length; i < limit; i++)
                inputBuffer.WriteByte(0x0);

            Module.Memory.SetArray(destinationPointer, inputBuffer.ToArray());

#if DEBUG
            _logger.Info($"Copied {inputBuffer.Length} bytes from {sourcePointer} to {destinationPointer}");
#endif
            Registers.AX = destinationPointer.Offset;
            Registers.DX = destinationPointer.Segment;
        }

        /// <summary>
        ///     Registers the Module with the MajorBBS system
        ///
        ///     Signature: int register_module(struct module *mod)
        ///     Return: AX = Value of usrptr->state whenever user is 'in' this module
        /// </summary>
        private void register_module()
        {
            var destinationPointer = GetParameterPointer(0);

            var moduleStruct = Module.Memory.GetArray(destinationPointer, 61);

            var relocationRecords =
                Module.File.SegmentTable.First(x => x.Ordinal == destinationPointer.Segment).RelocationRecords;

            //Description for Main Menu
            var moduleDescription = Encoding.Default.GetString(moduleStruct.ToArray(), 0, 25);
            Module.ModuleDescription = moduleDescription;
#if DEBUG
            _logger.Info($"Module Description set to {moduleDescription}");
#endif

            var moduleRoutines = new[]
                {"lonrou", "sttrou", "stsrou", "injrou", "lofrou", "huprou", "mcurou", "dlarou", "finrou"};

            for (var i = 0; i < 9; i++)
            {
                var currentOffset = (ushort)(25 + (i * 4));

                var routineEntryPoint = new byte[4];
                Array.Copy(moduleStruct.ToArray(), currentOffset, routineEntryPoint, 0, 4);

                //Setup the Entry Points in the Module
                Module.EntryPoints[moduleRoutines[i]] = new IntPtr16(BitConverter.ToUInt16(routineEntryPoint, 2), BitConverter.ToUInt16(routineEntryPoint, 0));

#if DEBUG
                _logger.Info(
                    $"Routine {moduleRoutines[i]} set to {BitConverter.ToUInt16(routineEntryPoint, 2):X4}:{BitConverter.ToUInt16(routineEntryPoint, 0):X4}");
#endif
            }

            //usrptr->state is the Module Number in use, as assigned by the host process
            //Because we only support 1 module running at a time right now, we just set this to one
            //TODO -- Update this to support multiple modules concurrently
            Registers.AX = 1;
        }

        /// <summary>
        ///     Opens the specified CNF file (.MCV in runtime form)
        ///
        ///     Signature: FILE *mbkprt=opnmsg(char *fileName)
        ///     Return: AX = Offset in Segment
        ///             DX = Host Segment
        /// </summary>
        private void opnmsg()
        {
            var sourcePointer = GetParameterPointer(0);

            var msgFileName = Encoding.Default.GetString(Module.Memory.GetString(sourcePointer, true));

            var offset = McvPointerDictionary.Allocate(new McvFile(msgFileName, Module.ModulePath));

            if(_currentMcvFile != null)
                _previousMcvFile.Enqueue(_currentMcvFile);

            _currentMcvFile = new IntPtr16(ushort.MaxValue, (ushort)offset);

#if DEBUG
            _logger.Info(
                $"Opened MSG file: {msgFileName}, assigned to {ushort.MaxValue:X4}:{offset:X4}");
#endif
            Registers.AX = _currentMcvFile.Offset;
            Registers.DX = _currentMcvFile.Segment;
        }

        /// <summary>
        ///     Retrieves a numeric option from MCV file
        ///
        ///     Signature: int numopt(int msgnum,int floor,int ceiling)
        ///     Return: AX = Value retrieved
        /// </summary>
        private void numopt()
        {
            var msgnum = GetParameter(0);
            var floor = (short)GetParameter(1);
            var ceiling = GetParameter(2);

            var outputValue = McvPointerDictionary[_currentMcvFile.Offset].GetNumeric(msgnum);

            //Validate
            if (outputValue < floor || outputValue > ceiling)
                throw new ArgumentOutOfRangeException($"{msgnum} value {outputValue} is outside specified bounds");

#if DEBUG
            _logger.Info($"Retrieved option {msgnum} value: {outputValue}");
#endif

            Registers.AX = (ushort)outputValue;
        }

        /// <summary>
        ///     Retrieves a yes/no option from an MCV file
        ///
        ///     Signature: int ynopt(int msgnum)
        ///     Return: AX = 1/Yes, 0/No
        /// </summary>
        private void ynopt()
        {
            var msgnum = GetParameter(0);

            var outputValue = McvPointerDictionary[_currentMcvFile.Offset].GetBool(msgnum);

#if DEBUG
            _logger.Info($"Retrieved option {msgnum} value: {outputValue}");
#endif

            Registers.AX = (ushort)(outputValue ? 1 : 0);
        }

        /// <summary>
        ///     Gets a long (32-bit) numeric option from the MCV File
        ///
        ///     Signature: long lngopt(int msgnum,long floor,long ceiling)
        ///     Return: AX = Most Significant 16-Bits
        ///             DX = Least Significant 16-Bits
        /// </summary>
        private void lngopt()
        {
            var msgnum = GetParameter(0);

            var floorLow = GetParameter(1);
            var floorHigh = GetParameter(2);

            var ceilingLow = GetParameter(3);
            var ceilingHigh = GetParameter(4);

            var floor = floorHigh << 16 | floorLow;
            var ceiling = ceilingHigh << 16 | ceilingLow;

            var outputValue = McvPointerDictionary[_currentMcvFile.Offset].GetLong(msgnum);

            //Validate
            if (outputValue < floor || outputValue > ceiling)
                throw new ArgumentOutOfRangeException($"{msgnum} value {outputValue} is outside specified bounds");

#if DEBUG
            _logger.Info($"Retrieved option {msgnum} value: {outputValue}");
#endif

            Registers.DX = (ushort)(outputValue >> 16);
            Registers.AX = (ushort)(outputValue & 0xFFFF);
        }

        /// <summary>
        ///     Gets a string from an MCV file
        ///
        ///     Signature: char *string=stgopt(int msgnum)
        ///     Return: AX = Offset in Segment
        ///             DX = Host Segment     
        /// </summary>
        private void stgopt()
        {
            var msgnum = GetParameter(0);

            if (!Module.Memory.TryGetVariable($"STGOPT_{_currentMcvFile.Offset}_{msgnum}", out var variablePointer))
            {
                var outputValue = McvPointerDictionary[_currentMcvFile.Offset].GetString(msgnum);

                variablePointer = base.Module.Memory.AllocateVariable($"STGOPT_{_currentMcvFile.Offset}_{msgnum}", (ushort)outputValue.Length);

                //Set Value in Memory
                Module.Memory.SetArray(variablePointer, outputValue);

#if DEBUG
                _logger.Info(
                    $"Retrieved option {msgnum} string value: {outputValue.Length} bytes saved to {variablePointer}");
#endif
            }
            Registers.AX = variablePointer.Offset;
            Registers.DX = variablePointer.Segment;
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
        private void getasc()
        {
#if DEBUG
            _logger.Info($"Called, redirecting to stgopt()");
#endif
            stgopt();
        }

        /// <summary>
        ///     Converts a long to an ASCII string
        ///
        ///     Signature: char *l2as(long longin)
        ///     Return: AX = Offset in Segment
        ///             DX = Host Segment 
        /// </summary>
        private void l2as()
        {
            var lowByte = GetParameter(0);
            var highByte = GetParameter(1);

            var outputValue = $"{highByte << 16 | lowByte}\0";

            if (!Module.Memory.TryGetVariable($"L2AS", out var variablePointer))
            {
                //Pre-allocate space for the maximum number of characters for a ulong
                variablePointer = base.Module.Memory.AllocateVariable("L2AS", 0xFF);
            }

            Module.Memory.SetArray(variablePointer, Encoding.Default.GetBytes(outputValue));

#if DEBUG
            _logger.Info(
                $"Received value: {outputValue}, string saved to {variablePointer}");
#endif

            Registers.AX = variablePointer.Offset;
            Registers.DX = variablePointer.Segment;
        }

        /// <summary>
        ///     Converts string to a long integer
        ///
        ///     Signature: long int atol(const char *str)
        ///     Return: AX = Offset in Segment
        ///             DX = Host Segment  
        /// </summary>
        private void atol()
        {
            var sourcePointer = GetParameterPointer(0);
            var stringToLong = Module.Memory.GetString(sourcePointer, true);


            if (!int.TryParse(Encoding.Default.GetString(stringToLong), out var outputValue))
            {
                /*
                 * Unsuccessful parsing returns a 0 value
                 * More info: http://www.cplusplus.com/reference/cstdlib/atol/
                 */
#if DEBUG
                _logger.Warn($"atol(): Unable to cast string value located at {sourcePointer} to long");
#endif
                Registers.AX = 0;
                Registers.DX = 0;
                Registers.F.SetFlag(EnumFlags.CF);
                return;
            }

#if DEBUG
            _logger.Info($"Cast {Encoding.Default.GetString(stringToLong)} ({sourcePointer}) to long");
#endif

            Registers.DX = (ushort)(outputValue >> 16);
            Registers.AX = (ushort)(outputValue & 0xFFFF);
            Registers.F.ClearFlag(EnumFlags.CF);
        }

        /// <summary>
        ///     Find out today's date coded as YYYYYYYMMMMDDDDD
        ///
        ///     Signature: int date=today()
        ///     Return: AX = Packed Date
        /// </summary>
        private void today()
        {
            //From DOSFACE.H:
            //#define dddate(mon,day,year) (((mon)<<5)+(day)+(((year)-1980)<<9))
            var packedDate = (DateTime.Now.Month << 5) + DateTime.Now.Day + ((DateTime.Now.Year - 1980) << 9);

#if DEBUG
            _logger.Info($"Returned packed date: {packedDate}");
#endif

            Registers.AX = (ushort)packedDate;
        }

        /// <summary>
        ///     Copies Struct into another Struct (Borland C++ Implicit Function)
        ///     CX contains the number of bytes to be copied
        ///
        ///     Signature: None -- Compiler Generated
        ///     Return: None
        /// </summary>
        private void f_scopy()
        {
            var sourcePointer = GetParameterPointer(0);
            var destinationPointer = GetParameterPointer(2);

            var sourceString = Module.Memory.GetArray(sourcePointer, Registers.CX);

            Module.Memory.SetArray(destinationPointer, sourceString);

#if DEBUG
            _logger.Info($"Copied {sourceString.Length} bytes from {sourcePointer} to {destinationPointer}");
#endif

            //Because this is an ASM routine, we have to manually fix the stack
            var previousBP = Module.Memory.GetWord(Registers.SS, (ushort)(Registers.BP + 1));
            var previousIP = Module.Memory.GetWord(Registers.SS, (ushort)(Registers.BP + 3));
            var previousCS = Module.Memory.GetWord(Registers.SS, (ushort)(Registers.BP + 5));
            //Set stack back to entry state, minus parameters
            Registers.SP += 14;
            Module.Memory.SetWord(Registers.SS, (ushort)(Registers.SP - 1), previousCS);
            Registers.SP -= 2;
            Module.Memory.SetWord(Registers.SS, (ushort)(Registers.SP - 1), previousIP);
            Registers.SP -= 2;
            Module.Memory.SetWord(Registers.SS, (ushort)(Registers.SP - 1), previousBP);
            Registers.SP -= 2;
            Registers.BP = Registers.SP;
        }

        /// <summary>
        ///     Case ignoring string match
        ///
        ///     Signature: int match=sameas(char *stgl, char* stg2)
        ///     Returns: AX = 1 if match
        /// </summary>
        private void sameas()
        {
            var string1Pointer = GetParameterPointer(0);
            var string2Pointer = GetParameterPointer(2);

            var string1 = Encoding.ASCII.GetString(Module.Memory.GetString(string1Pointer, true)).ToUpper();
            var string2 = Encoding.ASCII.GetString(Module.Memory.GetString(string2Pointer, true)).ToUpper();

            //Quick Check
            if (string1.Length != string2.Length)
            {
                Registers.AX = 0;
                return;
            }

            var result = true;
            //Deep Check -- at this point we know they're the same length
            for (var i = 0; i < string1.Length; i++)
            {
                if (string1[i] == string2[i])
                    continue;

                result = false;
                break;
            }

#if DEBUG
            _logger.Info($"Returned {result} comparing {string1} ({string1Pointer}) to {string2} ({string2Pointer})");
#endif

            Registers.AX = (ushort) (result ? 1 : 0);
        }

        /// <summary>
        ///     Property with the User Number (Channel) of the user currently being serviced
        ///
        ///     Signature: int usrnum
        ///     Retrurns: int == User Number (Channel)
        /// </summary>
        /// <returns></returns>
        private ReadOnlySpan<byte> usernum => base.Module.Memory.GetVariable("USERNUM").ToSpan();

        /// <summary>
        ///     Gets the online user account info
        /// 
        ///     Signature: struct usracc *uaptr=uacoff(unum)
        ///     Return: AX = Offset in Segment
        ///             DX = Host Segment  
        /// </summary>
        /// <returns></returns>
        private void uacoff()
        {
            var userNumber = GetParameter(0);

            if (!Module.Memory.TryGetVariable($"USRACC-{userNumber}", out var variablePointer))
                variablePointer = Module.Memory.AllocateVariable($"USRACC-{userNumber}", 0x155);

            Module.Memory.SetArray(variablePointer, ChannelDictionary[userNumber].UsrAcc.ToSpan());

            Registers.AX = variablePointer.Offset;
            Registers.DX = variablePointer.Segment;
        }

        /// <summary>
        ///     Like printf(), except the converted text goes into a buffer
        ///
        ///     Signature: void prf(string)
        /// </summary>
        /// <returns></returns>
        private void prf()
        {
            var sourcePointer = GetParameterPointer(0);

            var output = Module.Memory.GetString(sourcePointer);

            //If the supplied string has any control characters for formatting, process them
            var formattedMessage = FormatPrintf(output, 2);

            var pointer = Module.Memory.GetVariable("PRFBUF");
            Module.Memory.SetArray(pointer.Segment, (ushort)(_outputBufferPosition + pointer.Offset), formattedMessage);
            _outputBufferPosition += formattedMessage.Length;

#if DEBUG
            _logger.Info($"Added {formattedMessage.Length} bytes to the buffer");
#endif
        }


        /// <summary>
        ///     Resets prf buffer
        /// </summary>
        /// <returns></returns>
        private void clrprf()
        {
            _outputBufferPosition = 0;

#if DEBUG
            _logger.Info("Reset Output Buffer");
#endif
        }

        /// <summary>
        ///     Send prfbuf to a channel & clear
        ///
        ///     Signature: void outprf (unum)
        /// </summary>
        /// <returns></returns>
        private void outprf()
        {
            var userChannel = GetParameter(0);
            var pointer = Module.Memory.GetVariable("PRFBUF");
            ChannelDictionary[userChannel].DataToClient.Enqueue(Module.Memory.GetArray(pointer, (ushort)_outputBufferPosition).ToArray());
            _outputBufferPosition = 0;
        }

        /// <summary>
        ///     Deduct real credits from online acct
        ///
        ///     Signature: int enuf=dedcrd(long amount, int asmuch)
        ///     Returns: AX = 1 == Had enough, 0 == Not enough
        /// </summary>
        /// <returns></returns>
        private void dedcrd()
        {
            var sourceOffset = GetParameter(0);
            var lowByte = GetParameter(1);
            var highByte = GetParameter(2);

            var creditsToDeduct = (highByte << 16 | lowByte);

#if DEBUG
            _logger.Info($"Deducted {creditsToDeduct} from the current users account (unlimited)");
#endif

            Registers.AX = 1;
        }

        /// <summary>
        ///     Points to that channels 'user' struct
        ///
        ///     Signature: struct user *usrptr;
        /// </summary>
        /// <returns></returns>
        private ReadOnlySpan<byte> usrptr
        {
            get
            {
                var pointer = Module.Memory.GetVariable("USER");
                pointer.Offset += (ushort)(_channelNumber * 41);
                Module.Memory.SetArray(Module.Memory.GetVariable("USRPTR"), pointer.ToSpan());
                return Module.Memory.GetVariable("USRPTR").ToSpan();
            }
        }

        /// <summary>
        ///     Like prf(), but the control string comes from an .MCV file
        ///
        ///     Signature: void prfmsg(msgnum,p1,p2, ..• ,pn);
        /// </summary>
        /// <returns></returns>
        private void prfmsg()
        {
            var messageNumber = GetParameter(0);

            if (!McvPointerDictionary[_currentMcvFile.Offset].Messages.TryGetValue(messageNumber, out var outputMessage))
            {
                _logger.Warn($"prfmsg() unable to locate message number {messageNumber} in current MCV file {McvPointerDictionary[_currentMcvFile.Offset].FileName}");
                return;
            }

            var formattedMessage = FormatPrintf(outputMessage, 1);

            var variablePointer = Module.Memory.GetVariable("PRFBUF");

            Module.Memory.SetArray(variablePointer.Segment, (ushort)(variablePointer.Offset + _outputBufferPosition), formattedMessage);
            _outputBufferPosition += formattedMessage.Length;

#if DEBUG
            _logger.Info($"Added {formattedMessage.Length} bytes to the buffer from message number {messageNumber}");
#endif
        }

        /// <summary>
        ///     Displays a message in the Audit Trail
        ///
        ///     Signature: void shocst(char *summary, char *detail, p1, p1,...,pn);
        /// </summary>
        /// <returns></returns>
        private void shocst()
        {
            var string1Pointer = GetParameterPointer(0);
            var string2Pointer = GetParameterPointer(0);

            var stringSummary = Module.Memory.GetString(string1Pointer);
            var stringDetail = Module.Memory.GetString(string2Pointer);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.BackgroundColor = ConsoleColor.Blue;
            Console.WriteLine($"AUDIT SUMMARY: {Encoding.ASCII.GetString(stringSummary)}");
            Console.WriteLine($"AUDIT DETAIL: {Encoding.ASCII.GetString(stringDetail)}");
            Console.ResetColor();
        }

        /// <summary>
        ///     Array of chanel codes (as displayed)
        ///
        ///     Signature: int *channel
        /// </summary>
        /// <returns></returns>
        private ReadOnlySpan<byte> channel
        {
            get
            {
                var pointer = Module.Memory.GetVariable("CHANNEL");

                //Update the Channel Array with the latest channel/user info
                var arrayOutput = new byte[0x1FE];
                ushort channelsWritten = 0;
                foreach (var k in ChannelDictionary.Keys)
                {
                    Array.Copy(BitConverter.GetBytes((ushort)k), 0, arrayOutput, k + (2 * channelsWritten++), 2);
                }
                Module.Memory.SetArray(pointer, arrayOutput);
                return pointer.ToSpan();
            }
        }

        /// <summary>
        ///     Post credits to the specified Users Account
        ///
        ///     signature: int addcrd(char *keyuid,char *tckstg,int real)
        /// </summary>
        /// <returns></returns>
        private void addcrd()
        {
            var string1Pointer = GetParameterPointer(0);
            var string2Pointer = GetParameterPointer(2);
            var real = GetParameter(4);

            var string1 = Module.Memory.GetString(string1Pointer);
            var string2 = Module.Memory.GetString(string2Pointer);

#if DEBUG
            _logger.Info($"Added {Encoding.Default.GetString(string2)} credits to user account {Encoding.Default.GetString(string1)} (unlimited -- this function is ignored)");
#endif
        }

        /// <summary>
        ///     Converts an integer value to a null-terminated string using the specified base and stores the result in the array given by str
        ///
        ///     Signature: char *itoa(int value, char * str, int base)
        /// </summary>
        private void itoa()
        {
            var integerValue = GetParameter(0);
            var string1Pointer = GetParameterPointer(1);
            var baseValue = GetParameter(3);

            var output = Convert.ToString((short)integerValue, baseValue);
            output += "\0";

            Module.Memory.SetArray(string1Pointer, Encoding.Default.GetBytes(output));

#if DEBUG
            _logger.Info(
                $"Convterted integer {integerValue} to {output} (base {baseValue}) and saved it to {string1Pointer}");
#endif
        }

        /// <summary>
        ///     Does the user have the specified key
        /// 
        ///     Signature: int haskey(char *lock)
        ///     Returns: AX = 1 == True
        /// </summary>
        /// <returns></returns>
        private void haskey()
        {
            var lockNamePointer = GetParameterPointer(0);
            var lockNameBytes = Module.Memory.GetString(lockNamePointer);
            var lockName = Encoding.ASCII.GetString(lockNameBytes.Slice(0, lockNameBytes.Length - 1));

            Registers.AX = 1;
        }

        /// <summary>
        ///     Returns if the user has the key specified in an offline Security and Accounting option
        ///
        ///     Signature: int hasmkey(int msgnum)
        ///     Returns: AX = 1 == True
        /// </summary>
        /// <returns></returns>
        private void hasmkey()
        {
            var key = GetParameter(0);
            Registers.AX = 1;
        }

        /// <summary>
        ///     Returns a pseudo-random integral number in the range between 0 and RAND_MAX.
        ///
        ///     Signature: int rand (void)
        ///     Returns: AX = 16-bit Random Number
        /// </summary>
        /// <returns></returns>
        private void rand()
        {
            var randomValue = new Random(Guid.NewGuid().GetHashCode()).Next(1, short.MaxValue);

#if DEBUG
            _logger.Info($"Generated random number {randomValue} and saved it to AX");
#endif
            Registers.AX = (ushort)randomValue;
        }

        /// <summary>
        ///     Returns Packed Date as a char* in 'MM/DD/YY' format
        ///
        ///     Signature: char *ascdat=ncdate(int date)
        ///     Return: AX = Offset in Segment
        ///             DX = Host Segment  
        /// </summary>
        /// <returns></returns>
        private void ncdate()
        {
            /* From DOSFACE.H:
                #define ddyear(date) ((((date)>>9)&0x007F)+1980)
                #define ddmon(date)   (((date)>>5)&0x000F)
                #define ddday(date)    ((date)    &0x001F)
             */

            var packedDate = GetParameter(0);

            //Pack the Date
            var year = ((packedDate >> 9) & 0x007F) + 1980;
            var month = (packedDate >> 5) & 0x000F;
            var day = packedDate & 0x001F;
            var outputDate = $"{month:D2}/{day:D2}/{year % 100}\0";

            if (!Module.Memory.TryGetVariable("NCDATE", out var variablePointer))
            {
                variablePointer = Module.Memory.AllocateVariable("NCDATE", (ushort)outputDate.Length);
            }

            Module.Memory.SetArray(variablePointer.Segment, variablePointer.Offset, Encoding.Default.GetBytes(outputDate));

#if DEBUG
            _logger.Info($"Received value: {packedDate}, decoded string {outputDate} saved to {variablePointer.Segment:X4}:{variablePointer.Offset:X4}");
#endif
            Registers.AX = variablePointer.Offset;
            Registers.DX = variablePointer.Segment;
        }

        /// <summary>
        ///     Default Status Handler for status conditions this module is not specifically expecting
        ///
        ///     Ignored for now
        /// 
        ///     Signature: void dfsthn()
        /// </summary>
        /// <returns></returns>
        private void dfsthn()
        {

        }

        /// <summary>
        ///     Closes the Specified Message File
        ///
        ///     Signature: void clsmsg(FILE *mbkprt)
        /// </summary>
        /// <returns></returns>
        private void clsmsg()
        {
            var filePointer = GetParameterPointer(0);

#if DEBUG
            _logger.Info($"Closing MCV File: {filePointer}");
#endif

            McvPointerDictionary.Remove(filePointer.Offset);
        }

        /// <summary>
        ///     Register a real-time routine that needs to execute more than 1 time per second
        ///
        ///     Routines registered this way are executed at 18hz
        /// 
        ///     Signature: void rtihdlr(void (*rouptr)(void))
        /// </summary>
        private void rtihdlr()
        {
            var routinePointerOffset = GetParameter(0);
            var routinePointerSegment = GetParameter(1);

            var routine = new RealTimeRoutine(routinePointerSegment, routinePointerOffset);
            var routineNumber = Module.RtihdlrRoutines.Allocate(routine);
            Module.EntryPoints.Add($"RTIHDLR-{routineNumber}", routine);
#if DEBUG
            _logger.Info($"Registered routine {routinePointerSegment:X4}:{routinePointerOffset:X4}");
#endif
        }

        /// <summary>
        ///     'Kicks Off' the specified routine after the specified delay
        ///
        ///     Signature: void rtkick(int time, void *rouptr())
        /// </summary>
        /// <returns></returns>
        private void rtkick()
        {
            var delaySeconds = GetParameter(0);
            var routinePointer = GetParameterPointer(1);

            var routine = new RealTimeRoutine(routinePointer.Segment, routinePointer.Offset, delaySeconds);
            var routineNumber = Module.RtkickRoutines.Allocate(routine);

            Module.EntryPoints.Add($"RTKICK-{routineNumber}", routine);

#if DEBUG
            _logger.Info($"Registered routine {routinePointer} to execute every {delaySeconds} seconds");
#endif
        }

        /// <summary>
        ///     Sets 'current' MCV file to the specified pointer
        ///
        ///     Signature: FILE *setmbk(mbkptr)
        /// </summary>
        /// <returns></returns>
        private void setmbk()
        {
            var mcvFilePointer = GetParameterPointer(0);

            if (mcvFilePointer.Segment != ushort.MaxValue && !McvPointerDictionary.ContainsKey(mcvFilePointer.Offset))
                throw new ArgumentException($"Invalid MCV File Pointer: {mcvFilePointer}");

            _previousMcvFile.Enqueue(_currentMcvFile);
            _currentMcvFile = mcvFilePointer;

#if DEBUG
            _logger.Info($"Set current MCV File: {McvPointerDictionary[_currentMcvFile.Offset].FileName} (Pointer: {mcvFilePointer})");
#endif
        }

        /// <summary>
        ///     Restore previous MCV file block ptr from before last setmbk() call
        /// 
        ///     Signature: void rstmbk()
        /// </summary>
        /// <returns></returns>
        private void rstmbk()
        {
            _currentMcvFile = _previousMcvFile.Dequeue();
        }

        /// <summary>
        ///     Opens a Btrieve file for I/O
        ///
        ///     Signature: BTVFILE *bbptr=opnbtv(char *filnae, int reclen)
        ///     Return: AX = Offset to File Pointer
        ///             DX = Host Btrieve Segment  
        /// </summary>
        /// <returns></returns>
        private void opnbtv()
        {
            var btrieveFilenamePointer = GetParameterPointer(0);
            var recordLength = GetParameter(2);

            var btrieveFilename = Module.Memory.GetString(btrieveFilenamePointer, true);

            var fileName = Encoding.ASCII.GetString(btrieveFilename);

            var btrieveFile = new BtrieveFile(fileName, Module.ModulePath, recordLength);

            var btrieveFilePointer = BtrievePointerDictionary.Allocate(btrieveFile);

            //Set it as current by default
            _currentBtrieveFile = BtrievePointerDictionary[btrieveFilePointer];

#if DEBUG
            _logger.Info($"Opened file {fileName} and allocated it to {ushort.MaxValue:X4}:{btrieveFilePointer:X4}");
#endif
            Registers.AX = (ushort)btrieveFilePointer;
            Registers.DX = ushort.MaxValue;
        }

        /// <summary>
        ///     Used to set the Btrieve file for all subsequent database functions
        ///
        ///     Signature: void setbtv(BTVFILE *bbprt)
        /// </summary>
        /// <returns></returns>
        private void setbtv()
        {
            var btrieveFilePointer = GetParameterPointer(0);

            if (_currentBtrieveFile != null)
                _previousBtrieveFile = _currentBtrieveFile;

            _currentBtrieveFile = BtrievePointerDictionary[btrieveFilePointer.Offset];

#if DEBUG
            _logger.Info($"Setting current Btrieve file to {_currentBtrieveFile.FileName} ({btrieveFilePointer})");
#endif
        }

        /// <summary>
        ///     'Step' based Btrieve operation
        ///
        ///     Signature: int stpbtv (void *recptr, int stpopt)
        ///     Returns: AX = 1 == Record Found, 0 == Database Empty
        /// </summary>
        /// <returns></returns>
        private void stpbtv()
        {
            if (_currentBtrieveFile == null)
                throw new FileNotFoundException("Current Btrieve file hasn't been set using SETBTV()");

            var btrieveRecordPointer = GetParameterPointer(0);
            var stpopt = GetParameter(2);

            ushort resultCode = 0;
            switch (stpopt)
            {
                case (ushort)EnumBtrieveOperationCodes.StepFirst:
                    resultCode = _currentBtrieveFile.StepFirst();
                    break;
                case (ushort)EnumBtrieveOperationCodes.StepNext:
                    resultCode = _currentBtrieveFile.StepNext();
                    break;
                default:
                    throw new InvalidEnumArgumentException($"Unknown Btrieve Operation Code: {stpopt}");
            }

            //Set Memory Values
            if (resultCode > 0)
            {
                //See if the segment lives on the host or in the module
                Module.Memory.SetArray(btrieveRecordPointer, _currentBtrieveFile.GetRecord());
            }

            Registers.AX = resultCode;

#if DEBUG
            _logger.Info($"Performed Btrieve Step - Record written to {btrieveRecordPointer}, AX: {resultCode}");
#endif
        }

        /// <summary>
        ///     Restores the last Btrieve data block for use
        ///
        ///     Signature: void rstbtv (void)
        /// </summary>
        /// <returns></returns>
        private void rstbtv()
        {
            if (_previousBtrieveFile == null)
            {
#if DEBUG
                _logger.Info($"Previous Btrieve file == null, ignoring");
#endif
                return;
            }

            _currentBtrieveFile = _previousBtrieveFile;

#if DEBUG
            _logger.Info($"Set current Btreieve file to {_previousBtrieveFile.FileName}");
#endif
        }

        /// <summary>
        ///     Update the Btrieve current record
        ///
        ///     Signature: void updbtv(char *recptr)
        /// </summary>
        /// <returns></returns>
        private void updbtv()
        {
            var btrieveRecordPointerPointer = GetParameterPointer(0);

            //See if the segment lives on the host or in the module
            using var btrieveRecord = new MemoryStream();
            btrieveRecord.Write(Module.Memory.GetArray(btrieveRecordPointerPointer,
                _currentBtrieveFile.RecordLength));

            _currentBtrieveFile.Update(btrieveRecord.ToArray());

#if DEBUG
            _logger.Info(
                $"Updated current Btrieve record ({_currentBtrieveFile.CurrentRecordNumber}) with {btrieveRecord.Length} bytes");
#endif
        }

        /// <summary>
        ///     Insert new fixed-length Btrieve record
        /// 
        ///     Signature: void insbtv(char *recptr)
        /// </summary>
        /// <returns></returns>
        private void insbtv()
        {
            var btrieveRecordPointer = GetParameterPointer(0);

            //See if the segment lives on the host or in the module
            using var btrieveRecord = new MemoryStream();
            btrieveRecord.Write(Module.Memory.GetArray(btrieveRecordPointer,
                    _currentBtrieveFile.RecordLength));

            _currentBtrieveFile.Insert(btrieveRecord.ToArray());

#if DEBUG
            _logger.Info(
                $"Inserted Btrieve record at {_currentBtrieveFile.CurrentRecordNumber} with {btrieveRecord.Length} bytes");
#endif
        }

        /// <summary>
        ///     Raw status from btusts, where appropriate
        ///     
        ///     Signature: int status
        ///     Returns: Segment holding the Users Status
        /// </summary>
        /// <returns></returns>
        private ReadOnlySpan<byte> status => Module.Memory.GetVariable("STATUS").ToSpan();

        /// <summary>
        ///     Deduct real credits from online acct
        ///
        ///     Signature: int enuf=rdedcrd(long amount, int asmuch)
        ///     Returns: Always 1, meaning enough credits
        /// </summary>
        /// <returns></returns>
        private void rdedcrd()
        {
            Registers.AX = 1;
        }

        /// <summary>
        ///     sprintf-like string formatter utility
        ///
        ///     Main differentiation is that spr() supports long integer and floating point conversions
        /// </summary>
        /// <returns></returns>
        private void spr()
        {
            var sourcePointer = GetParameterPointer(0);

            var output = Module.Memory.GetString(sourcePointer);


            //If the supplied string has any control characters for formatting, process them
            var formattedMessage = FormatPrintf(output, 2);

            if (formattedMessage.Length > 0x400)
                throw new OutOfMemoryException($"SPR write is > 1k ({formattedMessage.Length}) and would overflow pre-allocated buffer");

            if (!Module.Memory.TryGetVariable("SPR", out var variablePointer))
            {
                //allocate 1k for the SPR buffer
                variablePointer = base.Module.Memory.AllocateVariable("SPR", 0x400);
            }

            Module.Memory.SetArray(variablePointer.Segment, variablePointer.Offset, formattedMessage);

#if DEBUG
            _logger.Info($"Added {formattedMessage.Length} bytes to the buffer");
#endif

            Registers.AX = variablePointer.Offset;
            Registers.DX = variablePointer.Segment;
        }

        /// <summary>
        ///     Long Multiplication (Borland C++ Implicit Function)
        /// 
        /// </summary>
        /// <returns></returns>
        private void f_lxmul()
        {

            var value1 = (Registers.DX << 16) | Registers.AX;
            var value2 = (Registers.CX << 16) | Registers.BX;

            var result = value1 * value2;

#if DEBUG
            _logger.Info($"Performed Long Multiplication {value1}*{value2}={result}");
#endif

            Registers.DX = (ushort)(result >> 16);
            Registers.AX = (ushort)(result & 0xFFFF);
        }

        /// <summary>
        ///     Long Division (Borland C++ Implicit Function)
        ///
        ///     Input: Two long values on stack (arg1/arg2)
        ///     Output: DX:AX = quotient
        ///             DI:SI = remainder
        /// </summary>
        /// <returns></returns>
        private void f_ldiv()
        {

            var arg1 = (GetParameter(1) << 16) | GetParameter(0);
            var arg2 = (GetParameter(3) << 16) | GetParameter(2);

            var quotient = Math.DivRem(arg1, arg2, out var remainder);

#if DEBUG
            _logger.Info($"Performed Long Division {arg1}/{arg2}={quotient} (Remainder: {remainder})");
#endif

            Registers.DX = (ushort)(quotient >> 16);
            Registers.AX = (ushort)(quotient & 0xFFFF);

            Registers.DI = (ushort)(remainder >> 16);
            Registers.SI = (ushort)(remainder & 0xFFFF);

            var previousBP = Module.Memory.GetWord(Registers.SS, (ushort)(Registers.BP + 1));
            var previousIP = Module.Memory.GetWord(Registers.SS, (ushort)(Registers.BP + 3));
            var previousCS = Module.Memory.GetWord(Registers.SS, (ushort)(Registers.BP + 5));
            //Set stack back to entry state, minus parameters
            Registers.SP += 14;
            Module.Memory.SetWord(Registers.SS, (ushort)(Registers.SP - 1), previousCS);
            Registers.SP -= 2;
            Module.Memory.SetWord(Registers.SS, (ushort)(Registers.SP - 1), previousIP);
            Registers.SP -= 2;
            Module.Memory.SetWord(Registers.SS, (ushort)(Registers.SP - 1), previousBP);
            Registers.SP -= 2;
            Registers.BP = Registers.SP;
        }

        /// <summary>
        ///     Writes formatted data from variable argument list to string
        ///
        ///     similar to prf, but the destination is a char*, but the output buffer
        /// </summary>
        /// <returns></returns>
        private void vsprintf()
        {
            var targetOffset = GetParameter(0);
            var targetSegment = GetParameter(1);
            var formatOffset = GetParameter(2);
            var formatSegment = GetParameter(3);

            var formatString = Module.Memory.GetString(formatSegment, formatOffset);

            //If the supplied string has any control characters for formatting, process them
            var formattedMessage = FormatPrintf(formatString, 4, true);


            Module.Memory.SetArray(targetSegment, targetOffset, formattedMessage);

            Registers.AX = (ushort)formattedMessage.Length;
        }

        /// <summary>
        ///     Scans the specified MDF file for a line prefix that matches the specified string
        /// 
        ///     Signature: char *scnmdf(char *mdfnam,char *linpfx);
        ///     Returns: AX = Offset of String
        ///              DX = Segment of String
        /// </summary>
        /// <returns></returns>
        private void scnmdf()
        {
            var mdfnameOffset = GetParameter(0);
            var mdfnameSegment = GetParameter(1);
            var lineprefixOffset = GetParameter(2);
            var lineprefixSegment = GetParameter(3);

            var mdfNameBytes = Module.Memory.GetString(mdfnameSegment, mdfnameOffset);
            var mdfName = Encoding.ASCII.GetString(mdfNameBytes);

            var lineprefixBytes = Module.Memory.GetString(lineprefixSegment, lineprefixOffset);
            var lineprefix = Encoding.ASCII.GetString(lineprefixBytes);

            //Setup Host Memory Variables Pointer
            if (!Module.Memory.TryGetVariable($"SCNMDF", out var variablePointer))
            {
                variablePointer = base.Module.Memory.AllocateVariable("SCNMDF", 0xFF);
            }

            var recordFound = false;
            foreach (var line in File.ReadAllLines($"{Module.ModulePath}{mdfName}"))
            {
                if (line.StartsWith(lineprefix))
                {
                    var result = Encoding.ASCII.GetBytes(line.Split(':')[1] + "\0");

                    if (result.Length > 256)
                        throw new OverflowException("SCNMDF result is > 256 bytes");

                    Module.Memory.SetArray(variablePointer.Segment, variablePointer.Offset, result);
                    recordFound = true;
                    break;
                }
            }

            //Write Null String to address if nothing was found
            if (!recordFound)
                Module.Memory.SetByte(variablePointer.Segment, variablePointer.Offset, 0x0);

            Registers.DX = variablePointer.Offset;
            Registers.AX = variablePointer.Segment;
        }

        /// <summary>
        ///     Returns the time of day it is bitwise HHHHHMMMMMMSSSSS coding
        ///
        ///     Signature: int time=now()
        /// </summary>
        /// <returns></returns>
        private void now()
        {
            //From DOSFACE.H:
            //#define dttime(hour,min,sec) (((hour)<<11)+((min)<<5)+((sec)>>1))
            var packedTime = (DateTime.Now.Hour << 11) + (DateTime.Now.Minute << 5) + (DateTime.Now.Second >> 1);

#if DEBUG
            _logger.Info($"Returned packed time: {packedTime}");
#endif

            Registers.AX = (ushort)packedTime;
        }

        /// <summary>
        ///     Modulo, non-significant (Borland C++ Implicit Function)
        ///
        ///     Signature: DX:AX = arg1 % arg2
        /// </summary>
        /// <returns></returns>
        private void f_lumod()
        {
            var arg1 = (GetParameter(1) << 16) | GetParameter(0);
            var arg2 = (GetParameter(3) << 16) | GetParameter(2);

            var result = arg1 % arg2;

            Registers.DX = (ushort)(result >> 16);
            Registers.AX = (ushort)(result & 0xFFFF);


            var previousBP = Module.Memory.GetWord(Registers.SS, (ushort)(Registers.BP + 1));
            var previousIP = Module.Memory.GetWord(Registers.SS, (ushort)(Registers.BP + 3));
            var previousCS = Module.Memory.GetWord(Registers.SS, (ushort)(Registers.BP + 5));
            //Set stack back to entry state, minus parameters
            Registers.SP += 14;
            Module.Memory.SetWord(Registers.SS, (ushort)(Registers.SP - 1), previousCS);
            Registers.SP -= 2;
            Module.Memory.SetWord(Registers.SS, (ushort)(Registers.SP - 1), previousIP);
            Registers.SP -= 2;
            Module.Memory.SetWord(Registers.SS, (ushort)(Registers.SP - 1), previousBP);
            Registers.SP -= 2;
            Registers.BP = Registers.SP;
        }

        /// <summary>
        ///     Set a block of memory to a value
        ///
        ///     Signature: void setmem(char *destination, unsigned nbytes, char value)
        /// </summary>
        /// <returns></returns>
        private void setmem()
        {
            var destinationOffset = GetParameter(0);
            var destinationSegment = GetParameter(1);
            var numberOfBytesToWrite = GetParameter(2);
            var byteToWrite = GetParameter(3);

            for (var i = 0; i < numberOfBytesToWrite; i++)
            {
                Module.Memory.SetByte(destinationSegment, (ushort)(destinationOffset + i), (byte)byteToWrite);
            }

#if DEBUG
            _logger.Info(
                $"Set {numberOfBytesToWrite} bytes to {byteToWrite:X2} starting at {destinationSegment:X4}:{destinationOffset:X4}");
#endif
        }

        /// <summary>
        ///     Output buffer of prf() and prfmsg()
        ///
        ///     Because it's a pointer, the segment only contains Int16:Int16 pointer to the actual prfbuf segment
        ///
        ///     Signature: char *prfbuf
        /// </summary>
        private ReadOnlySpan<byte> prfbuf => Module.Memory.GetVariable("PRFBUF").ToSpan();

        /// <summary>
        ///     Copies characters from a string
        ///
        ///     Signature: char *strncpy(char *destination, const char *source, size_t num)
        /// </summary>
        /// <returns></returns>
        private void strncpy()
        {
            var destinationOffset = GetParameter(0);
            var destinationSegment = GetParameter(1);
            var sourceOffset = GetParameter(2);
            var sourceSegment = GetParameter(3);
            var numberOfBytesToCopy = GetParameter(4);

            var sourceString = Module.Memory.GetString(sourceSegment, sourceOffset);

            for (var i = 0; i < numberOfBytesToCopy; i++)
            {
                if (sourceString[i] == 0x0)
                {
                    //Write remaining nulls
                    for (var j = i; j < numberOfBytesToCopy; j++)
                        Module.Memory.SetByte(destinationSegment, (ushort)(destinationOffset + j), 0x0);

                    break;
                }
                Module.Memory.SetByte(destinationSegment, (ushort)(destinationOffset + i), sourceString[i]);
            }

#if DEBUG
            _logger.Info($"Copied {numberOfBytesToCopy} from {sourceSegment:X4}:{sourceOffset:X4} to {destinationSegment:X4}:{destinationOffset:X4}");
#endif
        }

        private void register_textvar()
        {
            var textPointer = GetParameterPointer(0);
            var functionPointer = GetParameterPointer(2);

            var textBytes = Module.Memory.GetString(textPointer);

            _textVariables.Add(textBytes.ToArray(), functionPointer);

#if DEBUG
            _logger.Info($"Registered Textvar \"{Encoding.ASCII.GetString(textBytes)}\" to {functionPointer}");
#endif
        }

        /// <summary>
        ///     As best I can tell, this routine performs a GetEqual based on the key specified
        ///
        ///     Signature: int obtbtvl (void *recptr, void *key, int keynum, int obtopt, int loktyp)
        ///     Returns: AX == 0 record not found, 1 record found
        /// </summary>
        /// <returns></returns>
        private void obtbtvl()
        {
            var recordPointerOffset = GetParameter(0);
            var recordPointerSegment = GetParameter(1);
            var keyOffset = GetParameter(2);
            var keySegment = GetParameter(3);
            var keyNum = GetParameter(4);
            var obtopt = GetParameter(5);
            var lockType = GetParameter(6);

            ushort result = 0;
            switch (obtopt)
            {
                //GetEqual
                case 5:
                    result = (ushort)(_currentBtrieveFile.GetRecordByKey(Module.Memory.GetString(keySegment, keyOffset)) ? 1 : 0);
                    break;
            }

            Registers.AX = result;
        }

        /// <summary>
        ///     Declare size of the Volatile Data Area (Maximum size the module will require)
        ///     Because this is just another memory block, we use the host memory
        /// 
        ///     Signature: void *dclvda(unsigned nbytes);
        /// </summary>
        private void dclvda()
        {
            var size = GetParameter(0);

            if (size > 0x800)
                throw new OutOfMemoryException("Volatile Memory declaration > 2k");

#if DEBUG
            _logger.Info($"Volatile Memory Size requested of {size} bytes (2048 bytes currently allocated per channel)");
#endif
        }

        private ReadOnlySpan<byte> nterms => Module.Memory.GetVariable("NTERMS").ToSpan();

        /// <summary>
        ///     Compute volatile data pointer for the specified User Number
        ///
        ///     Because UserNumber and Channels in MBBSEmu are the same, we can just use the usernum AS channel
        ///
        ///     Signature: char *vdaoff(int unum)
        ///
        ///     Returns: AX == Segment of Volatile Data
        ///              DX == Offset of Volatile Data
        /// </summary>
        /// <returns></returns>
        private void vdaoff()
        {
            var channel = GetParameter(0);
            if (!Module.Memory.TryGetVariable($"VOLATILE-{channel}", out var variablePointer))
            {
                variablePointer = Module.Memory.AllocateVariable($"VOLATILE-{channel}", 0xA00);
            }

            Registers.DX = variablePointer.Offset;
            Registers.AX = variablePointer.Segment;
        }

        /// <summary>
        ///     Contains information about the current user account (class, state, baud, etc.)
        ///
        ///     Signature: struct user;
        /// </summary>
        /// <returns></returns>
        private ReadOnlySpan<byte> user
        {
            get
            {
                var pointer = Module.Memory.GetVariable("USER");
                pointer.Offset += (ushort)(41 * _channelNumber);
                return pointer.ToSpan();
            }
        }

        /// <summary>
        ///     Points to the Volatile Data Area for the current channel
        ///
        ///     Signature: char *vdaptr
        /// </summary>
        private ReadOnlySpan<byte> vdaptr => Module.Memory.GetVariable($"VDAPTR").ToSpan();

        /// <summary>
        ///     After calling bgncnc(), the command is unparsed (has spaces again, not separate words),
        ///     and prepared for interpretation using the command concatenation utilities
        ///
        ///     Signature: void bgncnc()
        /// </summary>
        private void bgncnc()
        {
            //TODO -- Dunno what I should do with this
        }

        /// <summary>
        ///     Number of Words in the users input line
        ///
        ///     Signature: int margc
        /// </summary>
        private ReadOnlySpan<byte> margc => Module.Memory.GetVariable("MARGC").ToSpan();

        /// <summary>
        ///     Returns the pointer to the next parsed input command from the user
        ///     If this is the first time it's called, it returns the first command
        /// </summary>
        private ReadOnlySpan<byte> nxtcmd
        {
            get
            {
                var variablePointer = Module.Memory.GetVariable("INPUT");
                var pointerSegment = Module.Memory.GetVariable("MARGN");
                if (_margvPointers.Count == 0 || _inputCurrentCommand > _margvPointers.Count)
                {
#if DEBUG
                    _logger.Info(
                        $"No Input, returning pointer to 1st (null) byte in Input Memory ({variablePointer.Segment:X4}:{variablePointer.Offset})");
#endif
                }
                else
                {
#if DEBUG
                    _logger.Info(
                        $"Returning Next Command ({_inputCurrentCommand} ({_margvPointers[_inputCurrentCommand++].Segment:X4}:{_margvPointers[_inputCurrentCommand++].Offset})");
#endif
                }
                return new IntPtr16(Module.Memory.GetArray(pointerSegment.Segment, (ushort)(pointerSegment.Offset + (_inputCurrentCommand * 4)), 4)).ToSpan();
            }
        }

        /// <summary>
        ///     Case-ignoring substring match
        ///
        ///     Signature: int match=sameto(char *shorts, char *longs)
        ///     Returns: AX == 1, match
        /// </summary>
        private void sameto()
        {
            var string1Offset = GetParameter(0);
            var string1Segment = GetParameter(1);
            var string2Offset = GetParameter(2);
            var string2Segment = GetParameter(3);

            using var string1InputBuffer = new MemoryStream();
            string1InputBuffer.Write(Module.Memory.GetString(string1Segment, string1Offset));
            var string1InputValue = Encoding.Default.GetString(string1InputBuffer.ToArray()).ToUpper();

            using var string2InputBuffer = new MemoryStream();
            string2InputBuffer.Write(Module.Memory.GetString(string2Segment, string2Offset));
            var string2InputValue = Encoding.Default.GetString(string2InputBuffer.ToArray()).ToUpper();

            var resultValue = string1InputValue.Equals(string2InputValue, StringComparison.InvariantCultureIgnoreCase);

#if DEBUG
            _logger.Info($"Returned {resultValue} comparing {string1InputValue} ({string1Segment:X4}:{string1Offset:X4}) to {string2InputValue} ({string2Segment:X4}:{string2Offset:X4})");
#endif

            Registers.AX = (ushort)(resultValue ? 1 : 0);
        }

        /// <summary>
        ///     Expect a Character from the user (character from the current command)
        /// </summary>
        private void cncchr()
        {
            Registers.AX = 0;
        }

        /// <summary>
        ///     Pointer to the current position in prfbuf
        ///
        ///     Signature: char *prfptr;
        /// </summary>
        private ReadOnlySpan<byte> prfptr
        {
            get
            {
                var pointer = Module.Memory.GetVariable("PRFPTR");
                var prfbufPointer = Module.Memory.GetVariable("PRFBUF");
                Module.Memory.SetArray(pointer, new IntPtr16(prfbufPointer.Segment, (ushort)(prfbufPointer.Offset + _outputBufferPosition)).ToSpan());
                return pointer.ToSpan();
            }
        }

        /// <summary>
        ///     Opens a new file for reading/writing
        ///
        ///     Signature: file* fopen(const char* filename, USE)
        /// </summary>
        private void f_open()
        {
            var filenamePointer = GetParameterPointer(0);
            var modePointer = GetParameterPointer(2);

            var filenameInputBuffer = Module.Memory.GetString(filenamePointer, true);
            var filenameInputValue = Encoding.Default.GetString(filenameInputBuffer.ToArray()).ToUpper();

            var modeInputBuffer = Module.Memory.GetString(modePointer, true);
            var fileAccessMode = FileStruct.CreateFlagsEnum(modeInputBuffer);

            //Allocate Memory for FILE struct
            if (!Module.Memory.TryGetVariable($"FILE_{filenameInputValue}", out var fileStructPointer))
                fileStructPointer = Module.Memory.AllocateVariable($"FILE_{filenameInputValue}", FileStruct.Size);
            
            //Write New Blank Pointer
            var fileStruct = new FileStruct();
            Module.Memory.SetArray(fileStructPointer, fileStruct.ToSpan());

            if (!File.Exists($"{Module.ModulePath}{filenameInputValue}"))
            {
                if (fileAccessMode.HasFlag(FileStruct.EnumFileAccessFlags.Read))
                {
#if DEBUG
                    _logger.Warn($"Unable to find file {Module.ModulePath}{filenameInputValue}");
#endif
                    Registers.AX = fileStruct.curp.Offset;
                    Registers.DX = fileStruct.curp.Segment;
                    return;
                }

                //Create a new file for W or A
                File.Create($"{Module.ModulePath}{filenameInputValue}").Dispose();
#if DEBUG
                _logger.Info($"Creating new file {filenameInputValue}");
#endif
            }
            else
            {
                //Overwrite existing file for W
                if (fileAccessMode.HasFlag(FileStruct.EnumFileAccessFlags.Write))
                {
#if DEBUG
                    _logger.Info($"Overwritting file {filenameInputValue}");
#endif
                    File.Create($"{Module.ModulePath}{filenameInputValue}").Dispose();
                }
            }

#if DEBUG
            _logger.Info($"Opening File: {filenameInputValue}");
#endif
            //Setup the File Stream
            var fileStreamPointer =  FilePointerDictionary.Allocate(File.Open($"{Module.ModulePath}{filenameInputValue}", FileMode.OpenOrCreate));
            
            //Set Struct Values
            fileStruct.SetFlags(fileAccessMode);
            fileStruct.curp = new IntPtr16(ushort.MaxValue, (ushort) fileStreamPointer);
            Module.Memory.SetArray(fileStructPointer, fileStruct.ToSpan());

            Registers.AX = fileStructPointer.Offset;
            Registers.DX = fileStructPointer.Segment;
        }

        /// <summary>
        ///     Closes an Open File Pointer
        ///
        ///     Signature: int fclose(FILE* stream )
        /// </summary>
        private void f_close()
        {
            var filePointer = GetParameterPointer(0);

            var fileStruct = new FileStruct(Module.Memory.GetArray(filePointer, FileStruct.Size));

            if (fileStruct.curp.Segment == 0 && fileStruct.curp.Offset == 0)
            {
#if DEBUG
                _logger.Warn($"Called FCLOSE on null File Stream Pointer (0000:0000), usually means it tried to open a file that doesn't exist");
                Registers.AX = 0;
                return;
#endif
            }

            if (!FilePointerDictionary.ContainsKey(fileStruct.curp.Offset))
                throw new Exception($"Attempted to call FCLOSE on pointer not in File Stream Segment {fileStruct.curp}");

            //Clean Up File Stream Pointer
            FilePointerDictionary[fileStruct.curp.Offset].Dispose();
            FilePointerDictionary.Remove(fileStruct.curp.Offset);

#if DEBUG
            _logger.Info($"Closed File {filePointer} (Stream: {fileStruct.curp})");
#endif
            Registers.AX = 0;
        }

        /// <summary>
        ///     sprintf() function in C++ to handle string formatting
        ///
        ///     Signature: int sprintf(char *str, const char *format, ... )
        /// </summary>
        private void sprintf()
        {
            var destinationOffset = GetParameter(0);
            var destinationSegment = GetParameter(1);
            var sourceOffset = GetParameter(2);
            var sourceSegment = GetParameter(3);


            var output = Module.Memory.GetString(sourceSegment, sourceOffset);

            //If the supplied string has any control characters for formatting, process them
            var formattedMessage = FormatPrintf(output, 4);

            Module.Memory.SetArray(destinationSegment, destinationOffset, formattedMessage);

#if DEBUG
            _logger.Info($"Added {output.Length} bytes to the buffer");
#endif

        }

        /// <summary>
        ///     Looks for the specified filename (filespec) in the BBS directory, returns 1 if the file is there
        /// 
        ///     Signature: int yes=fndlst(struct fndblk &fb, filespec, char attr)
        /// </summary>
        private void fnd1st()
        {
            var findBlockPointer = GetParameterPointer(0);
            var filespecPointer = GetParameterPointer(2);
            var attrChar = GetParameter(4);

            var fileName = Module.Memory.GetString(filespecPointer, true);
            var fileNameString = Encoding.ASCII.GetString(fileName);
            Registers.AX = (ushort)(File.Exists($"{Module.ModulePath}{fileNameString}") ? 1 : 0);
        }

        /// <summary>
        ///     Reads an array of count elements, each one with a size of size bytes, from the stream and stores
        ///     them in the block of memory specified by ptr.
        ///
        ///     Signature: size_t fread(void* ptr, size_t size, size_t count, FILE* stream)
        /// </summary>
        private void f_read()
        {
            var destinationPointer = GetParameterPointer(0);
            var size = GetParameter(2);
            var count = GetParameter(3);
            var fileStructPointer = GetParameterPointer(4);

            var fileStruct = new FileStruct(Module.Memory.GetArray(fileStructPointer, FileStruct.Size));

            if (!FilePointerDictionary.TryGetValue(fileStruct.curp.Offset, out var fileStream))
                throw new FileNotFoundException($"File Stream Pointer for {fileStructPointer} (Stream: {fileStruct.curp}) not found in the File Pointer Dictionary");

            ushort elementsRead = 0;
            for (var i = 0; i < count; i++)
            {
                var dataRead = new byte[size];
                var bytesRead = fileStream.Read(dataRead);

                if (bytesRead != size)
                    break;

                Module.Memory.SetArray(destinationPointer.Segment, (ushort) (destinationPointer.Offset + (i * size)),
                    dataRead);
                elementsRead++;
            }

            //Update EOF Flag if required
            if (fileStream.Position == fileStream.Length)
            {
                fileStruct.flags |= (ushort) FileStruct.EnumFileFlags.EOF;
                Module.Memory.SetArray(fileStructPointer, fileStruct.ToSpan());
            }

#if DEBUG
            _logger.Info($"Read {elementsRead} group(s) of {size} bytes from {fileStructPointer} (Stream: {fileStruct.curp}), written to {destinationPointer}");
#endif

            Registers.AX = elementsRead;
        }

        /// <summary>
        ///     Writes an array of count elements, each one with a size of size bytes, from the block of memory
        ///     pointed by ptr to the current position in the stream.
        /// </summary>
        private void f_write()
        {
            var sourcePointer = GetParameterPointer(0);
            var size = GetParameter(2);
            var count = GetParameter(3);
            var fileStructPointer = GetParameterPointer(4);

            var fileStruct = new FileStruct(Module.Memory.GetArray(fileStructPointer, FileStruct.Size));

            if (!FilePointerDictionary.TryGetValue(fileStruct.curp.Offset, out var fileStream))
                throw new FileNotFoundException($"File Pointer {fileStructPointer} (Stream: {fileStruct.curp}) not found in the File Pointer Dictionary");

            ushort elementsWritten = 0;
            for (var i = 0; i < count; i++)
            {
                fileStream.Write(Module.Memory.GetArray(sourcePointer.Segment, (ushort)(sourcePointer.Offset + (i * size)), size));
                elementsWritten++;
            }

            //Update EOF Flag if required
            if (fileStream.Position == fileStream.Length)
            {
                fileStruct.flags |= (ushort)FileStruct.EnumFileFlags.EOF;
                Module.Memory.SetArray(fileStructPointer, fileStruct.ToSpan());
            }

#if DEBUG
            _logger.Info($"Read {elementsWritten} group(s) of {size} bytes from {sourcePointer}, written to {fileStructPointer} (Stream: {fileStruct.curp})");
#endif
            Registers.AX = elementsWritten;
        }

        /// <summary>
        ///     Deleted the specified file
        /// </summary>
        private void unlink()
        {
            var filenameOffset = GetParameter(0);
            var filenameSegment = GetParameter(1);

            using var filenameInputBuffer = new MemoryStream();
            filenameInputBuffer.Write(Module.Memory.GetString(filenameSegment, filenameOffset, true));
            var filenameInputValue = Encoding.Default.GetString(filenameInputBuffer.ToArray()).ToUpper();

#if DEBUG
            _logger.Info($"Deleting File: {Module.ModulePath}{filenameInputValue}");
#endif

            if (File.Exists($"{Module.ModulePath}{filenameInputValue}"))
                File.Delete($"{Module.ModulePath}{filenameInputValue}");

            Registers.AX = 0;
        }

        /// <summary>
        ///     Returns the length of the C string str
        ///
        ///     Signature: size_t strlen(const char* str)
        /// </summary>
        private void strlen()
        {
            var stringPointer = GetParameterPointer(0);

            var stringValue = Module.Memory.GetString(stringPointer, true);

#if DEBUG
            _logger.Info(
                $"Evaluated string length of {stringValue.Length} for string at {stringPointer}: {Encoding.ASCII.GetString(stringValue)}");
#endif

            Registers.AX = (ushort)stringValue.Length;
        }

        /// <summary>
        ///     User Input
        ///
        ///     Signature: char input[]
        /// </summary>
        private ReadOnlySpan<byte> input => Module.Memory.GetVariable("INPUT").ToSpan();

        /// <summary>
        ///     Converts a lowercase letter to uppercase
        ///
        ///     Signature: int toupper (int c)
        /// </summary>
        private void toupper()
        {
            var character = GetParameter(0);
            if (character >= 97 && character <= 122)
            {
                Registers.AX = (ushort)(character - 32);
            }
            else
            {
                Registers.AX = character;
            }
#if DEBUG
            _logger.Info($"Converted {(char)character} to {(char)Registers.AX}");
#endif
        }

        /// <summary>
        ///     Converts a uppercase letter to lowercase
        ///
        ///     Signature: int tolower (int c)
        /// </summary>
        private void tolower()
        {
            var character = GetParameter(0);
            if (character >= 65 || character <= 90)
            {
                Registers.AX = (ushort)(character + 32);
            }
            else
            {
                Registers.AX = character;
            }
#if DEBUG
            _logger.Info($"Converted {(char)character} to {(char)Registers.AX}");
#endif
        }

        /// <summary>
        ///     Changes the name of the file or directory specified by old name to new name
        ///
        ///     Signature: int rename(const char *oldname, const char *newname )
        /// </summary>
        private void rename()
        {
            var oldFilenamePointer = GetParameterPointer(0);
            var newFilenamePointer = GetParameterPointer(2);

            var oldFilenameInputBuffer = Module.Memory.GetString(oldFilenamePointer, true);
            var oldFilenameInputValue = Encoding.Default.GetString(oldFilenameInputBuffer).ToUpper();

            var newFilenameInputBuffer = Module.Memory.GetString(newFilenamePointer, true);
            var newFilenameInputValue = Encoding.Default.GetString(newFilenameInputBuffer).ToUpper();

            if (!File.Exists($"{Module.ModulePath}{oldFilenameInputValue}"))
                throw new FileNotFoundException($"Attempted to rename file that doesn't exist: {oldFilenameInputValue}");

            File.Move($"{Module.ModulePath}{oldFilenameInputValue}", $"{Module.ModulePath}{newFilenameInputValue}");

#if DEBUG
            _logger.Info($"Renamed file {oldFilenameInputValue} to {newFilenameInputValue}");
#endif

            Registers.AX = 0;
        }


        /// <summary>
        ///     The Length of the total Input Buffer received
        ///
        ///     Signature: int inplen
        /// </summary>
        private ReadOnlySpan<byte> inplen => Module.Memory.GetVariable("INPLEN").ToSpan();

        private ReadOnlySpan<byte> margv => Module.Memory.GetVariable("MARGV").ToSpan();

        private ReadOnlySpan<byte> margn => Module.Memory.GetVariable("MARGN").ToSpan();

        /// <summary>
        ///     Restore parsed input line (undoes effects of parsin())
        ///
        ///     Signature: void rstrin()
        /// </summary>
        private void rstrin()
        {
            ChannelDictionary[_channelNumber].rstrin();

            var inputMemory = Module.Memory.GetVariable("INPUT");
            Module.Memory.SetArray(inputMemory.Segment, inputMemory.Offset, ChannelDictionary[_channelNumber].InputCommand);
        }

        private ReadOnlySpan<byte> _exitbuf => new byte[] { 0x0, 0x0, 0x0, 0x0 };
        private ReadOnlySpan<byte> _exitfopen => new byte[] { 0x0, 0x0, 0x0, 0x0 };
        private ReadOnlySpan<byte> _exitopen => new byte[] { 0x0, 0x0, 0x0, 0x0 };
        private ReadOnlySpan<byte> usaptr => Module.Memory.GetVariable("USAPTR").ToSpan();

        /// <summary>
        ///     Appends the first num characters of source to destination, plus a terminating null-character.
        ///
        ///     Signature: char *strncat(char *destination, const char *source, size_t num)
        /// </summary>
        private void strncat()
        {
            var destinationPointer = GetParameterPointer(0);
            var sourcePointer = GetParameterPointer(2);
            var bytesToCopy = GetParameter(4);

            var stringDestination = Module.Memory.GetString(destinationPointer);
            var stringSource = Module.Memory.GetString(sourcePointer, true);

            if (stringSource.Length < bytesToCopy)
                bytesToCopy = (ushort)stringSource.Length;

            var newString = new byte[stringDestination.Length + bytesToCopy];
            Array.Copy(stringSource.Slice(0, bytesToCopy).ToArray(), 0, newString, 0, bytesToCopy);
            Array.Copy(stringDestination.ToArray(), 0, newString, bytesToCopy, stringDestination.Length);

#if DEBUG
            _logger.Info($"Concatenated String 1 (Length:{stringSource.Length}b. Copied {bytesToCopy}b) with String 2 (Length: {stringDestination.Length}) to new String (Length: {newString.Length}b)");
#endif
            Module.Memory.SetArray(destinationPointer, newString);
        }

        /// <summary>
        ///     Sets the first num bytes of the block of memory with the specified value
        ///
        ///     Signature: void memset(void *ptr, int value, size_t num);
        /// </summary>
        private void memset()
        {
            var destinationPointer = GetParameterPointer(0);
            var valueToFill = GetParameter(2);
            var numberOfByteToFill = GetParameter(3);

            for (var i = 0; i < numberOfByteToFill; i++)
            {
                Module.Memory.SetByte(destinationPointer.Segment, (ushort)(destinationPointer.Offset + i), (byte)valueToFill);
            }

#if DEBUG
            _logger.Info($"Filled {numberOfByteToFill} bytes at {destinationPointer} with {(byte)valueToFill:X2}");
#endif
        }

        /// <summary>
        ///     Read value of CNF option
        ///
        ///     Signature: char *bufard=getmsg(msgnum)
        /// </summary>
        private void getmsg()
        {
            var msgnum = GetParameter(0);

            if (!Module.Memory.TryGetVariable($"GETMSG_{_currentMcvFile.Offset}_{msgnum}", out var variablePointer))
            {
                var outputValue = McvPointerDictionary[_currentMcvFile.Offset].GetString(msgnum);

                variablePointer = base.Module.Memory.AllocateVariable($"GETMSG_{_currentMcvFile.Offset}_{msgnum}", (ushort)outputValue.Length);

                //Set Value in Memory
                Module.Memory.SetArray(variablePointer, outputValue);

#if DEBUG
                //_logger.Info(
                //$"Retrieved option {msgnum} string value: {outputValue.Length} bytes saved to {variablePointer}");
#endif
            }

#if DEBUG
            _logger.Info( $"Retrieved option {msgnum} from file {_currentMcvFile}, already saved to {variablePointer}");
#endif

            Registers.AX = variablePointer.Offset;
            Registers.DX = variablePointer.Segment;
        }

        /// <summary>
        ///     Reads data from s and stores them accounting to parameter format into the locations given by the additional arguments
        ///
        ///     Signature: int sscanf(const char *s, const char *format, ...)
        /// </summary>
        private void sscanf()
        {
            var inputPointer = GetParameterPointer(0);
            var formatPointer = GetParameterPointer(2);

            var inputString = Module.Memory.GetString(inputPointer);
            var formatString = Module.Memory.GetString(formatPointer);

            sscanf(inputString, formatString, 4);


#if DEBUG
            //_logger.Info($"Processed sscanf on {Encoding.ASCII.GetString(formatString)}");
#endif
        }

        /// <summary>
        ///     General MS-DOS interrupt interface
        ///
        ///     These calls are INT21 calls made from the module
        ///
        ///     Signature: int intdos(union REGS * inregs, union REGS * outregs)
        /// </summary>
        private void intdos()
        {
            var parameterOffset1 = GetParameterPointer(0);
            var parameterOffset2 = GetParameterPointer(2);

            //Get the AH value to determine the function being called
            var registers = new CpuRegisters();
            registers.FromRegs(Module.Memory.GetArray(parameterOffset1, 16));
            switch (registers.AH)
            {
                case 0x2C:
                    //Get Time
                    {
                        var returnValue = new CpuRegisters
                        {
                            CH = (byte) DateTime.Now.Hour,
                            CL = (byte) DateTime.Now.Minute,
                            DH = (byte) DateTime.Now.Second,
                            DL = (byte) (DateTime.Now.Millisecond / 100)
                        };
                        Module.Memory.SetArray(parameterOffset2, returnValue.ToRegs());
                        return;
                    }
                default:
                    throw new ArgumentOutOfRangeException($"Unknown DOS INT21 API: {registers.AH:X2}");
            }
        }

        /// <summary>
        ///     Like pmlt(), but the control string comes from an .MCV file
        ///
        ///     Signature: void prfmlt(int msgno,...)
        /// </summary>
        private void prfmlt()
        {
            var messageNumber = GetParameter(0);

            var output = McvPointerDictionary[_currentMcvFile.Offset].GetString(messageNumber);

            //If the supplied string has any control characters for formatting, process them
            var formattedMessage = FormatPrintf(output, 1);

            var pointer = Module.Memory.GetVariable("PRFBUF");
            Module.Memory.SetArray(pointer.Segment, (ushort)(_outputBufferPosition + pointer.Offset), formattedMessage);
            _outputBufferPosition += formattedMessage.Length;

#if DEBUG
            _logger.Info($"Added {output.Length} bytes to the buffer");
#endif
        }

        /// <summary>
        ///     Clear the prf buffer indep of outprf
        ///     Basically the same as clrprf()
        ///
        ///     Signature: void clrmlt()
        /// </summary>
        private void clrmlt() => clrprf();

        /// <summary>
        ///     Registers a Global Command Handler -- any input in the BBS
        ///     (even outside the module) is run through this
        /// </summary>
        private void globalcmd()
        {
            GlobalCommandHandler = GetParameterPointer(0);

#if DEBUG
            _logger.Info($"Registered Global Command Handler at: {GlobalCommandHandler}");
#endif
        }

        /// <summary>
        ///     Catastro Failure, basically a mega show stopping error
        /// </summary>
        private void catastro()
        {
            var messagePointer = GetParameterPointer(0);
            var message = Module.Memory.GetString(messagePointer);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.BackgroundColor = ConsoleColor.Red;
            Console.WriteLine($"{Encoding.ASCII.GetString(message)}");
            Console.ResetColor();

            Registers.Halt = true;
        }

        /// <summary>
        ///     Reads the specified number of characters from the file until a new line or EOF
        ///
        ///     Signature: char* fgets(char* str, int num, FILE* stream )
        /// </summary>
        private void fgets()
        {
            var destinationPointer = GetParameterPointer(0);
            var maxCharactersToRead = GetParameter(2);
            var fileStructPointer = GetParameterPointer(3);

            var fileStruct = new FileStruct(Module.Memory.GetArray(fileStructPointer, FileStruct.Size));

            if(!FilePointerDictionary.TryGetValue(fileStruct.curp.Offset, out var fileStream))
                throw new Exception($"Unable to locate FileStream for {fileStructPointer} (Stream: {fileStruct.curp})");

            var startingPointer = new IntPtr16(destinationPointer.Segment, destinationPointer.Offset);
            for (var i = 0; i < maxCharactersToRead; i++)
            {
                var inputValue = (byte) fileStream.ReadByte();

                if (inputValue == '\n' || fileStream.Position == fileStream.Length)
                    break;

                Module.Memory.SetByte(destinationPointer.Segment, destinationPointer.Offset++,inputValue);
            }
            Module.Memory.SetByte(destinationPointer, 0x0);

            //Update EOF Flag if required
            if (fileStream.Position == fileStream.Length)
            {
                fileStruct.flags |= (ushort)FileStruct.EnumFileFlags.EOF;
                Module.Memory.SetArray(fileStructPointer, fileStruct.ToSpan());
            }

#if DEBUG
            _logger.Info($"Read string from {fileStructPointer} (Stream: {fileStruct.curp}), saved starting at {startingPointer}->{destinationPointer}");
#endif
        }

        /// <summary>
        ///     Concatenates two strings and saves them to the destination.
        ///
        ///     Signature: char *strcat(char *destination, const char *source)
        /// </summary>
        public void strcat()
        {
            var destinationPointer = GetParameterPointer(0);
            var sourcePointer = GetParameterPointer(2);

            var destinationString = Module.Memory.GetString(destinationPointer, true);
            var sourceString = Module.Memory.GetString(sourcePointer);

            Module.Memory.SetArray(destinationPointer.Segment, (ushort) (destinationPointer.Offset + destinationString.Length),
                sourceString);

#if DEBUG
            _logger.Info($"Concatenated strings {Encoding.ASCII.GetString(destinationString)} and {Encoding.ASCII.GetString(sourceString)} to {destinationPointer}");
#endif
        }

        /// <summary>
        ///     Writes the C string pointed by format to the stream
        ///
        ///     Signature: int fprintf ( FILE * stream, const char * format, ... )
        /// </summary>
        private void f_printf()
        {
            var fileStructPointer = GetParameterPointer(0);
            var sourcePointer = GetParameterPointer(2);

            var fileStruct = new FileStruct(Module.Memory.GetArray(fileStructPointer, FileStruct.Size));

            if(!FilePointerDictionary.TryGetValue(fileStruct.curp.Offset, out var fileStream))
                throw new Exception($"Unable to locate FileStream for {fileStructPointer} (Stream: {fileStruct.curp})");

            var output = Module.Memory.GetString(sourcePointer);

            //If the supplied string has any control characters for formatting, process them
            var formattedMessage = FormatPrintf(output, 4);

            fileStream.Write(formattedMessage);

            //Update EOF Flag if required
            if (fileStream.Position == fileStream.Length)
            {
                fileStruct.flags |= (ushort)FileStruct.EnumFileFlags.EOF;
                Module.Memory.SetArray(fileStructPointer, fileStruct.ToSpan());
            }

#if DEBUG
            _logger.Info($"Wrote {formattedMessage.Length} bytes to {fileStructPointer} (Stream: {fileStruct.curp})");
#endif
        }

        /// <summary>
        ///     Sets the position indicator associated with the stream to a new position.
        /// 
        ///     Signature: int fseek ( FILE * stream, long int offset, int origin )
        /// </summary>
        private void fseek()
        {
            var fileStructPointer = GetParameterPointer(0);
            var offset = GetParameterLong(2);
            var origin = GetParameter(4);

            var fileStruct = new FileStruct(Module.Memory.GetArray(fileStructPointer, FileStruct.Size));

            if (!FilePointerDictionary.TryGetValue(fileStruct.curp.Offset, out var fileStream))
                throw new Exception($"Unable to locate FileStream for {fileStructPointer} (Stream: {fileStruct.curp})");

            switch (origin)
            {
                case 2: //EOF
                    fileStream.Seek(offset, SeekOrigin.End);
                    break;
                case 1: //CUR
                    fileStream.Seek(offset, SeekOrigin.Current);
                    break;
                case 0: //SET
                    fileStream.Seek(offset, SeekOrigin.Begin);
                    break;
            }

            //Update EOF Flag if required
            if (fileStream.Position == fileStream.Length)
            {
                fileStruct.flags |= (ushort)FileStruct.EnumFileFlags.EOF;
                Module.Memory.SetArray(fileStructPointer, fileStruct.ToSpan());
            }

#if DEBUG
            _logger.Info($"Seek to {fileStream.Position} in {fileStructPointer} (Stream: {fileStruct.curp})");
#endif
        }

        /// <summary>
        ///     Takes a packed time from now() and returns it as 'HH:MM:SS'
        ///
        ///     Signature: char *asctim=nctime(int time)
        /// </summary>
        private void nctime()
        {
            //From DOSFACE.H:
            //#define dttime(hour,min,sec) (((hour)<<11)+((min)<<5)+((sec)>>1))
            //var packedTime = (DateTime.Now.Hour << 11) + (DateTime.Now.Minute << 5) + (DateTime.Now.Second >> 1);

            var packedTime = GetParameter(0);

            var unpackedHour = (packedTime >> 11) & 0x1F;
            var unpackedMinutes = (packedTime >> 5 & 0x1F);
            var unpackedSeconds = packedTime & 0xF;

            var unpackedTime = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, unpackedHour, unpackedMinutes, unpackedSeconds);

            var timeString = unpackedTime.ToString("HH:mm:ss");

            if (!Module.Memory.TryGetVariable("NTIME", out var variablePointer))
            {
                variablePointer = Module.Memory.AllocateVariable("NCTIME", (ushort)timeString.Length);
            }

            Module.Memory.SetArray(variablePointer.Segment, variablePointer.Offset, Encoding.Default.GetBytes(timeString));

#if DEBUG
            _logger.Info($"Received value: {packedTime}, decoded string {timeString} saved to {variablePointer}");
#endif
            Registers.AX = variablePointer.Offset;
            Registers.DX = variablePointer.Segment;
        }

        /// <summary>
        ///     Returns a pointer to the first occurence of character in the C string str
        ///
        ///     Signature: char * strchr ( const char * str, int character )
        /// 
        ///     More Info: http://www.cplusplus.com/reference/cstring/strchr/
        /// </summary>
        private void strchr()
        {
            var stringPointer = GetParameterPointer(0);
            var characterToFind = GetParameter(2);

            var stringToSearch = Module.Memory.GetString(stringPointer, true);

            for (var i = 0; i < stringToSearch.Length; i++)
            {
                if (stringToSearch[i] != (byte) characterToFind) continue;

                Registers.AX = (ushort) (stringPointer.Offset + i);
                Registers.DX = stringPointer.Segment;

#if DEBUG
                _logger.Info($"Found character {(char)characterToFind} at position {i} in string {stringPointer}");
#endif
                return;
            }

            //If character wasn't found, return a null pointer
            Registers.AX = 0;
            Registers.DX = 0;
        }

        /// <summary>
        ///     Parses the input line (null terminating each word)
        ///
        ///     Signature: void parsin()
        /// </summary>
        private void parsin()
        {
            ChannelDictionary[_channelNumber].parsin();

            var inputMemory = Module.Memory.GetVariable("INPUT");
            Module.Memory.SetArray(inputMemory.Segment, inputMemory.Offset, ChannelDictionary[_channelNumber].InputCommand);
        }

        /// <summary>
        ///     Copies the values of num bytes from the location pointed by source to the memory block pointed by destination.
        ///     Copying takes place as if an intermediate buffer were used, allowing the destination and source to overlap
        ///
        ///     Signature: void * memmove ( void * destination, const void * source, size_t num )
        ///
        ///     More Info: http://www.cplusplus.com/reference/cstring/memmove/
        /// </summary>
        private void movemem()
        {
            var sourcePointer = GetParameterPointer(0);
            var destinationPointer = GetParameterPointer(2);
            var bytesToMove = GetParameter(4);

            //Cast to array as the write can overlap and overwrite, mucking up the span read
            var sourceData = Module.Memory.GetArray(sourcePointer, bytesToMove).ToArray();

            Module.Memory.SetArray(destinationPointer, sourceData);

#if DEBUG
            _logger.Info($"Moved {bytesToMove} bytes {sourcePointer}->{destinationPointer}");
#endif
            Registers.AX = destinationPointer.Offset;
            Registers.DX = destinationPointer.Segment;
        }

        /// <summary>
        ///     Returns the current position in the specified FILE stream
        ///
        ///     Signature: long int ftell(FILE *stream );
        /// </summary>
        private void ftell()
        {
            var fileStructPointer = GetParameterPointer(0);

            var fileStruct = new FileStruct(Module.Memory.GetArray(fileStructPointer, FileStruct.Size));

            var currentPosition = FilePointerDictionary[fileStruct.curp.Offset].Position;

#if DEBUG
            _logger.Info($"Returning Current Position of {currentPosition} for {fileStructPointer} (Stream: {fileStruct.curp})");
#endif

            Registers.DX = (ushort)(currentPosition >> 16);
            Registers.AX = (ushort)(currentPosition & 0xFFFF);
        }

        /// <summary>
        ///     Determines if a user is using a specific module
        ///
        ///     Signature: int isin=instat(char *usrid, int qstate)
        /// </summary>
        private void instat()
        {
            var useridPointer = GetParameterPointer(0);
            var moduleId = GetParameter(2);

            var userId = Module.Memory.GetString(useridPointer).ToArray();

            var userSession = ChannelDictionary.Values.FirstOrDefault(x => userId.SequenceEqual(StringFromArray(x.UsrAcc.userid).ToArray()));

            //User not found?
            if (userSession == null || ChannelDictionary[userSession.Channel].UsrPtr.State != moduleId)
            {
                Registers.AX = 0;
                return;
            }

            var othusnPointer = Module.Memory.GetVariable("OTHUSN");
            Module.Memory.SetWord(othusnPointer, ChannelDictionary[userSession.Channel].Channel);
            Registers.AX = 1;
        }

        /// <summary>
        ///     Searches the string for any occurence of the substring
        ///
        ///     Signature: int found=samein(char *subs, char *string)
        /// </summary>
        private void samein()
        {
            var stringToFindPointer = GetParameterPointer(0);
            var stringToSearchPointer = GetParameterPointer(2);

            var stringToFind = Encoding.ASCII.GetString(Module.Memory.GetString(stringToFindPointer, true)).ToUpper();
            var stringToSearch = Encoding.ASCII.GetString(Module.Memory.GetString(stringToSearchPointer, true)).ToUpper();

            //Won't find it if the substring is greater than the string to search
            if (stringToFind.Length > stringToSearch.Length)
            {
                Registers.AX = 0;
                return;
            }

            for (var i = 0; i < stringToSearch.Length; i++)
            {
                //are there not enough charaters left to match?
                if (stringToFind.Length > (stringToSearch.Length - i))
                    break;

                var isMatch = true;
                for (var j = 0; j < stringToFind.Length; j++)
                {
                    if (stringToSearch[i + j] == stringToFind[j])
                        continue;

                    isMatch = false;
                    break;
                }

                //Found a match?
                if (isMatch)
                {
                    Registers.AX = 1;
                    return;
                }
            }

            Registers.AX = 0;
        }

        /// <summary>
        ///     Skip past whitespace, returns pointer to the first NULL or non-whitespace character in the string
        ///
        ///     Signature: char *skpwht(char *string)
        /// </summary>
        private void skpwht()
        {
            var stringToSearchPointer = GetParameterPointer(0);

            var stringToSearch = Module.Memory.GetString(stringToSearchPointer, false);

            for (var i = 0; i < stringToSearch.Length; i++)
            {
                if (stringToSearch[i] == 0x0 || stringToSearch[i] == 0x20) continue;

                Registers.AX = (ushort) (stringToSearchPointer.Offset + i);
                Registers.DX = stringToSearchPointer.Segment;
                return;
            }
            Registers.AX = stringToSearchPointer.Offset;
            Registers.DX = stringToSearchPointer.Segment;
        }

        /// <summary>
        ///     Just like the standard fgets(), except it uses '\r' as a line terminator (a hard carriage return on The Major BBS),
        ///     and it won't have a line terminator on the last line if the file doesn't have it.
        ///
        ///     Signature: char *mdfgets(char *buf,int size,FILE *fp)
        /// </summary>
        private void mdfgets()
        {
            var destinationPointer = GetParameterPointer(0);
            var maxSize = GetParameter(2);
            var fileStructPointer = GetParameterPointer(3);

            var fileStruct = new FileStruct(Module.Memory.GetArray(fileStructPointer, FileStruct.Size));

            if(!FilePointerDictionary.TryGetValue(fileStruct.curp.Offset, out var fileStream))
                throw new Exception($"Unable to locate FileStream for {fileStructPointer} (Stream: {fileStruct.curp})");

            var startingDestinationPointer = new IntPtr16(destinationPointer.Segment, destinationPointer.Offset);

            for (var i = 0; i < (maxSize-1); i++)
            {
                var inputByte = (byte)fileStream.ReadByte();

                //EOF or Line Terminator
                if (inputByte == 0x13 || fileStream.Position == fileStream.Length)
                    break;

                Module.Memory.SetByte(destinationPointer.Segment, destinationPointer.Offset++, inputByte);
            }
            Module.Memory.SetByte(destinationPointer.Segment, destinationPointer.Offset, 0x0);

#if DEBUG
      _logger.Info($"Read Line from {fileStructPointer} (Stream: {fileStruct.curp}) {startingDestinationPointer}->{destinationPointer}");          
#endif
        }

        private ReadOnlySpan<byte> genbb => Module.Memory.GetVariable("GENBB").ToSpan();

        /// <summary>
        ///     Turns echo on for this channel
        ///
        ///     Signature: void echon()
        /// </summary>
        private void echon()
        {
            ChannelDictionary[_channelNumber].TransparentMode = false;
        }

        /// <summary>
        ///     Converts all lowercase characters in a string to uppercase
        ///
        ///     Signature: char *upper=strlwr(char *string)
        /// </summary>
        private void strupr()
        {
            var stringToConvertPointer = GetParameterPointer(0);

            var stringData = Module.Memory.GetString(stringToConvertPointer).ToArray();

            for (var i = 0; i < stringData.Length; i++)
            {
                if (stringData[i] >= 97 && stringData[i] <= 122)
                    stringData[i] -= 32;
            }

#if DEBUG
            _logger.Info($"Converted string to {Encoding.ASCII.GetString(stringData)}");
#endif

            Module.Memory.SetArray(stringToConvertPointer, stringData);
        }

        /// <summary>
        ///     Returns a pointer to the first occurrence of str2 in str1, or a null pointer if str2 is not part of str1.
        /// </summary>
        private void strstr()
        {
            var stringToSearchPointer = GetParameterPointer(0);
            var stringToFindPointer = GetParameterPointer(2);

            var stringToSearch = Encoding.ASCII.GetString(Module.Memory.GetString(stringToSearchPointer, true)).ToUpper();
            var stringToFind = Encoding.ASCII.GetString(Module.Memory.GetString(stringToFindPointer, true)).ToUpper();

            //Won't find it if the substring is greater than the string to search
            if (stringToFind.Length > stringToSearch.Length)
            {
                Registers.AX = 0;
                return;
            }

            for (var i = 0; i < stringToSearch.Length; i++)
            {
                //are there not enough charaters left to match?
                if (stringToFind.Length > (stringToSearch.Length - i))
                    break;

                var isMatch = true;
                for (var j = 0; j < stringToFind.Length; j++)
                {
                    if (stringToSearch[i + j] == stringToFind[j])
                        continue;

                    isMatch = false;
                    break;
                }

                //Found a match?
                if (isMatch)
                {
                    Registers.AX = (ushort) (stringToSearchPointer.Offset + i);
                    Registers.DX = stringToSearchPointer.Segment;
                    return;
                }
            }

            Registers.AX = 0;
            Registers.DX = 0;
        }

        /// <summary>
        ///     Removes trailing blank spaces from string
        ///
        ///     Signature: int nremoved=depad(char *string)
        /// </summary>
        private void depad()
        {
            var stringPointer = GetParameterPointer(0);

            var stringToSearch = Module.Memory.GetString(stringPointer).ToArray();

            ushort numRemoved = 0;
            for (var i = 1; i < stringToSearch.Length; i++)
            {
                if (stringToSearch[^i] == 0x0)
                    continue;

                if (stringToSearch[^i] != 0x20)
                    break;

                stringToSearch[^i] = 0x0;
                numRemoved++;
            }

            Module.Memory.SetArray(stringPointer, stringToSearch);
            Registers.AX = numRemoved;
        }

        private ReadOnlySpan<byte> othusn => Module.Memory.GetVariable("OTHUSN").ToSpan();
    }
}