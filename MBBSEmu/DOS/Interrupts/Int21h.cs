﻿using MBBSEmu.CPU;
using MBBSEmu.Date;
using MBBSEmu.DOS;
using MBBSEmu.Extensions;
using MBBSEmu.IO;
using MBBSEmu.Memory;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MBBSEmu.DOS.Interrupts
{
    /// <summary>
    ///     Interrupt Vector 21h which handles the main DOS APIs
    ///
    ///     This is implemented within the DOS Kernel
    /// </summary>
    public class Int21h : IInterruptHandler
    {
        private ILogger _logger { get; init; }
        private CpuRegisters _registers { get; init; }
        private IMemoryCore _memory { get; init; }
        private IClock _clock { get; init; }
        private TextReader _stdin { get; init; }
        private TextWriter _stdout { get; init; }
        private TextWriter _stderr { get; init; }

        /// <summary>
        ///     Path of the current Execution Context
        /// </summary>
        private string _path { get; init; }

        /// <summary>
        ///     INT 21h defined Disk Transfer Area
        ///
        ///     Buffer used to hold information on the current Disk / IO operation
        /// </summary>
        private FarPtr DiskTransferArea;

        public byte Vector => 0x21;

        private readonly Dictionary<byte, FarPtr> _interruptVectors;

        public enum AllocationStrategy {
            FIRST_FIT = 0,
            BEST_FIT = 1,
            LAST_FIT = 2,
        }

        private AllocationStrategy _allocationStrategy = AllocationStrategy.BEST_FIT;

        public Int21h(CpuRegisters registers, IMemoryCore memory, IClock clock, ILogger logger, TextReader stdin, TextWriter stdout, TextWriter stderr, string path = "")
        {
            _registers = registers;
            _memory = memory;
            _clock = clock;
            _stdin = stdin;
            _stdout = stdout;
            _stderr = stderr;
            _interruptVectors = new Dictionary<byte, FarPtr>();
            _logger = logger;
            _path = path;
        }

        public void Handle()
        {
            //_logger.Error($"Interrupt AX {_registers.AX:X4} H:{_registers.AH:X2}");

            switch (_registers.AH)
            {
                case 0x01:
                    {
                        // DOS - KEYBOARD INPUT (with echo)
                        // Return: AL = character read
                        // TODO (check ^C/^BREAK) and if so EXECUTE int 23h
                        var c = (byte)_stdin.Read();
                        _stdout.Write((char)c);
                        _registers.AL = c;
                        return;
                    }
                case 0x67:
                    {
                        // DOS - SET HANDLE COUNT
                        // BX : Number of handles
                        // Return: carry set if error (and error code in AX)
                        _registers.F = _registers.F.ClearFlag((ushort)EnumFlags.CF);
                        return;
                    }
                case 0x48:
                    {
                        // DOS - Allocate memory
                        // BX = number of 16-byte paragraphs desired
                        // Return: CF set on error
                        //             AX = error code
                        //             BX = maximum available
                        //         CF clear if successful
                        //             AX = segment of allocated memory block
                        var ptr = _memory.Malloc((uint)(_registers.BX * 16));
                        if (!ptr.IsNull() && ptr.Offset != 0)
                            throw new DataMisalignedException("RealMode allocator returned memory not on segment boundary");

                        if (ptr.IsNull())
                        {
                            _registers.F = _registers.F.SetFlag((ushort)EnumFlags.CF);
                            _registers.BX = 0; // TODO get maximum available here
                            _registers.AX = (ushort)DOSErrorCode.INSUFFICIENT_MEMORY;
                        }
                        else
                        {
                            _registers.F = _registers.F.ClearFlag((ushort)EnumFlags.CF);
                            _registers.AX = ptr.Segment;
                        }

                        return;
                    }
                case 0x49:
                    {
                        // DOS - Free Memory
                        // ES = Segment address of area to be freed
                        // Return: CF set on error
                        //             AX = error code
                        //         CF clear if successful
                        _memory.Free(new FarPtr(_registers.ES, 0));
                        // no status, so always say we're good
                        _registers.F = _registers.F.ClearFlag((ushort)EnumFlags.CF);
                        return;
                    }
                case 0x58:
                    {
                        // INT 21 - AH = 58h DOS 3.x - GET/SET MEMORY ALLOCATION STRATEGY
                        // AL = function code
                        //     0 = get allocation strategy
                        //     1 = set allocation strategy
                        // BL = strategy code
                        //     0 first fit (use first memory block large enough)
                        //     1 best fit (use smallest memory block large enough)
                        //     2 last fit (use high part of last usable memory block)
                        // Return:
                        //   CF set on error
                        //     AX = error code
                        //   CF clear if successful
                        //     AX = strategy code
                        // Note: the Set subfunction accepts any value in BL; 2 or greater means last fit.
                        // the Get subfunction returns the last value set, so programs should check
                        // whether the value is >= 2, not just equal to 2.

                        if (_registers.AL == 0)
                        {
                            _registers.F = _registers.F.ClearFlag((ushort)EnumFlags.CF);
                            _registers.AX = (ushort)_allocationStrategy;
                        }
                        else if (_registers.AL == 1)
                        {
                            if (_registers.BL > 2)
                                _allocationStrategy = AllocationStrategy.LAST_FIT;
                            else
                                _allocationStrategy = (AllocationStrategy)_registers.BL;

                            _registers.F = _registers.F.ClearFlag((ushort)EnumFlags.CF);
                            _registers.AX = (ushort)_allocationStrategy;
                        }
                        else
                        {
                            _registers.F = _registers.F.SetFlag((ushort)EnumFlags.CF);
                            _registers.AX = (ushort)DOSErrorCode.UNKNOWN_COMMAND;
                        }
                        return;
                    }
                case 0x09:
                    {
                        var src = new FarPtr(_registers.DS, _registers.DX);
                        var memoryStream = new MemoryStream();
                        byte b;
                        while ((b = _memory.GetByte(src++)) != '$')
                            memoryStream.WriteByte(b);

                        _stdout.Write(Encoding.ASCII.GetString(memoryStream.ToArray()));
                        return;
                    }
                case 0x19:
                    {
                        //DOS - GET DEFAULT DISK NUMBER
                        //Return: AL = Drive Number
                        _registers.AL = 2; //C:
                        return;
                    }
                case 0x1A:
                    {
                        //Specifies the memory area to be used for subsequent FCB operations.
                        //DS:DX = Segment:offset of DTA
                        DiskTransferArea = new FarPtr(_registers.DS, _registers.DX);
                        return;
                    }
                case 0x25:
                    {
                        /*
                            INT 21 - AH = 25h DOS - SET INTERRUPT VECTOR
                            AL = interrupt number
                            DS:DX = new vector to be used for specified interrupt
                         */

                        var interruptVector = _registers.AL;
                        var newVectorPointer = new FarPtr(_registers.DS, _registers.DX);

                        _interruptVectors[interruptVector] = newVectorPointer;

                        return;
                    }
                case 0x2A:
                    {
                        //DOS - GET CURRENT DATE
                        //Return: DL = day, DH = month, CX = year
                        //AL = day of the week(0 = Sunday, 1 = Monday, etc.)
                        _registers.DL = (byte)_clock.Now.Day;
                        _registers.DH = (byte)_clock.Now.Month;
                        _registers.CX = (ushort)_clock.Now.Year;
                        _registers.AL = (byte)_clock.Now.DayOfWeek;
                        return;
                    }
                case 0x2C:
                    {
                        //DOS - GET CURRENT TIME
                        //Return: CH = hour, CL = minute, DH = second, DL = 1/100 seconds
                        _registers.CH = (byte) _clock.Now.Hour;
                        _registers.CL = (byte) _clock.Now.Minute;
                        _registers.DH = (byte) _clock.Now.Second;
                        _registers.DL = (byte) (_clock.Now.Millisecond / 100);
                        return;
                    }
                case 0x2F:
                    {
                        //Get DTA address
                        /*
                         *  Action:	Returns the segment:offset of the current DTA for read/write operations.
                            On entry:	AH = 2Fh
                            Returns:	ES:BX = Segment.offset of current DTA
                         */
                        DiskTransferArea = _memory.GetOrAllocateVariablePointer("Int21h-DTA", 0xFF);

                        _registers.ES = DiskTransferArea.Segment;
                        _registers.BX = DiskTransferArea.Offset;
                        return;
                    }
                case 0x30:
                    {
                        /*  DOS 2+ - GET DOS VERSION
                            AH = 30h
                            Return: AL = Major Version number (0 for DOS 1.x)
                            AH = Minor Version number
                            BH = OEM number
                             00h IBM
                             16h DEC
                            BL:CX = 24-bit user number
                         */
                        _registers.AL = 6;
                        _registers.AH = 22;
                        return;

                    }
                case 0x35:
                    {
                        /*
                           INT 21 - AH = 35h DOS 2+ - GET INTERRUPT VECTOR
                           AL = interrupt number
                           Return: ES:BX = value of interrupt vector
                         */

                        if (!_interruptVectors.TryGetValue(_registers.AL, out var resultVector))
                        {
                            _registers.ES = 0xFFFF;
                            _registers.BX = _registers.AL;
                        }
                        else
                        {
                            _registers.ES = resultVector.Segment;
                            _registers.BX = resultVector.Offset;
                        }
                        return;

                    }
                case 0x40:
                    {
                        /*
                          INT 21 - AH = 40h DOS 2+ - WRITE TO FILE WITH HANDLE
                            BX = file handle
                            CX = number of bytes to write
                            DS:DX -> buffer

                            Return: CF set on error
                             AX = error code

                            CF clear if successful
                             AX = number of bytes written

                            Note: if CX is zero, no data is written, and the file is truncated or extended
                             to the current position
                         */
                        var fileHandle = _registers.BX;
                        var numberOfBytes = _registers.CX;
                        var bufferPointer = new FarPtr(_registers.DS, _registers.DX);

                        var dataToWrite = _memory.GetArray(bufferPointer, numberOfBytes);

                        /*
                             DOS Default/Predefined Handles:
                             0 - Standard Input Device - can be redirected (STDIN)
	                         1 - Standard Output Device - can be redirected (STDOUT)
	                         2 - Standard Error Device - can be redirected (STDERR)
	                         3 - Standard Auxiliary Device (STDAUX)
	                         4 - Standard Printer Device (STDPRN)
                         */

                        if (fileHandle == 1)
                            _stdout.WriteLine(Encoding.ASCII.GetString(dataToWrite));
                        else if (fileHandle == 2)
                            _stderr.WriteLine(Encoding.ASCII.GetString(dataToWrite));

                        break;
                    }
                case 0x44:
                    {
                        /*
                            INT 21 - AH = 44H DOS Get Device Information

                            Sub-Function Definition is in AL
                         */
                        switch (_registers.AL)
                        {

                            case 0x0:
                                {
                                    /*
                                        INT 21 - AX = 4400h DOS 2+ - IOCTL - GET DEVICE INFORMATION
                                        BX = file or device handle
                                        Return: CF set on error
                                         AX = error code
                                        CF clear if successful
                                         DX = device info
                                     */

                                    //Device
                                    if (_registers.BX <= 2)
                                    {
                                        _registers.DX = 0;

                                        _registers.DX |= 1; //STD Input
                                        _registers.DX |= 1 << 1; //STD Output
                                        _registers.DX |= 1 << 4; //Reserved? DOSBox sets it
                                        _registers.DX |= 1 << 6; //Not EOF
                                        _registers.DX |= 1 << 7; //IS Device
                                        _registers.DX |= 1 << 15; //Reserved? DOSBox sets it
                                    }
                                }
                                break;
                        }

                        break;
                    }
                case 0x47:
                    {
                        /*
                            DOS 2+ - GET CURRENT DIRECTORY
                            DL = drive (0=default, 1=A, etc.)
                            DS:SI points to 64-byte buffer area
                            Return: CF set on error
                            AX = error code
                            Note: the returned path does not include the initial backslash
                         */
                        _memory.SetArray(_registers.DS, _registers.SI, Encoding.ASCII.GetBytes("BBSV6\0"));
                        _registers.AX = 0;
                        _registers.DL = 0;
                        _registers.F = _registers.F.ClearFlag((ushort)EnumFlags.CF);
                        return;
                    }
                case 0x4A:
                    {
                        /*
                            INT 21 - AH = 4Ah DOS 2+ - ADJUST MEMORY BLOCK SIZE (SETBLOCK)
                            ES = Segment address of block to change
                            BX = New size in paragraphs
                            Return: CF set on error
                            AX = error code
                            BX = maximum size possible for the block

                            Because MBBSEmu allocates blocks as 0xFFFF in length, we ignore this and proceed
                         */

                        var segmentToAdjust = _registers.ES;
                        var newSize = _registers.BX;

                        if (_memory is ProtectedModeMemoryCore)
                        {
                            ProtectedModeMemoryCore protectedMemory = (ProtectedModeMemoryCore)_memory;
                            if (!protectedMemory.HasSegment(segmentToAdjust))
                                protectedMemory.AddSegment(segmentToAdjust);
                        }

                        _registers.BX = 0xFFFF;
                        break;
                    }
                case 0x4C:
                    {
                        /*
                            INT 21 - AH = 4Ch DOS 2+ - QUIT WITH EXIT CODE (EXIT)
                            AL = exit code
                            Return: never returns
                         */
                        _stdout.WriteLine($"Exiting With Exit Code: {_registers.AL}");
                        _registers.Halt = true;
                        break;
                    }
                case 0x4E:
                {
                        /*
                         *INT 21 - AH = 4Eh DOS 2+ - FIND FIRST ASCIZ (FIND FIRST)
                            CX = search attributes
                            DS:DX -> ASCIZ filename
                            Return: CF set on error
                                AX = error code
                                [DTA] = data block
                                undocumented fields
                                    PC-DOS 3.10
                                         byte 00h: drive letter
                                         bytes 01h-0Bh: search template
                                         byte 0Ch: search attributes
                                    DOS 2.x (and DOS 3.x except 3.1???)
                                         byte 00h: search attributes
                                         byte 01h: drive letter
                                         bytes 02h-0Ch: search template
                                         bytes 0Dh-0Eh: entry count within directory
                                         bytes 0Fh-12h: reserved
                                         bytes 13h-14h: cluster number of parent directory
                                         byte 15h: attribute of file found
                                         bytes 16h-17h: file time
                                         bytes 18h-19h: file date
                                         bytes 1Ah-1Dh: file size
                                         bytes 1Eh-3Ah: ASCIZ filename+extension
                         */
                        var fileName = Encoding.ASCII.GetString(_memory.GetString(_registers.DS, _registers.DX, true));

                        var fileUtility = new FileUtility(_logger);
                        var foundFile = fileUtility.FindFile(_path, fileName);



                        if(!File.Exists($"{_path}{foundFile}"))
                            _registers.F = _registers.F.SetFlag((ushort)EnumFlags.CF);

                        break;
                }
                case 0x62:
                    {
                        /*
                            INT 21 - AH = 62h DOS 3.x - GET PSP ADDRESS
                            Return: BX = segment address of PSP

                            This is only set when an EXE is running, thus should only be called from
                            an EXE.
                         */
                        if (!_memory.TryGetVariablePointer("Int21h-PSP", out var pspPointer))
                            throw new Exception("No PSP has been defined");

                        _registers.BX = _memory.GetWord(pspPointer);
                        return;
                    }
                default:
                    throw new ArgumentOutOfRangeException($"Unsupported INT 21h Function: 0x{_registers.AH:X2}");
            }
        }
    }
}
