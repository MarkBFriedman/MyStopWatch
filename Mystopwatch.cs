// ==++==
// 
//   Copyright (c) Microsoft Corporation.  All rights reserved.
// 
// ==--==
/*============================================================
**
** Class:  Stopwatch
**
** Purpose: Implementation for a Stopwatch class that captures CPU Cycles beginning in Vista.
**
** Date:  Jan 18, 2007
**
===========================================================*/

namespace System.Diagnostics
{
    using Microsoft.Win32;
    using System;
    using System.Runtime.InteropServices;
    using System.Diagnostics;
    using System.Collections;


    // This class uses high-resolution performance counter if installed hardware 
    // does not support it. Otherwise, the class will fall back to DateTime class
    // and uses ticks as a measurement.

    internal class Win32
    {
        [DllImport("kernel32.dll")]
        public static extern bool QueryPerformanceFrequency(out long freq);

        [DllImport("kernel32.dll")]
        public static extern bool QueryPerformanceCounter(out long freq);

        [DllImport("kernel32.dll")] //This is the new Native API call to get CPU Cycles
        public static extern bool QueryThreadCycleTime(uint handle, out UInt64 ticks);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThread();

        [DllImport("kernel32.dll")]
        public static extern UInt32 GetCurrentProcessorNumber();
       
        [DllImport("powrprof.dll", SetLastError = true)] //Gets the current processor Clock speed
        public static extern UInt32 CallNtPowerInformation(
             Int32 InformationLevel,
             IntPtr lpInputBuffer,
             UInt32 nInputBufferSize,
             IntPtr lpOutputBuffer,
             UInt32 nOutputBufferSize
             );
    }

    internal struct PROCESSOR_POWER_INFORMATION //Struct returned by CallNtPowerInformation contains Current and MaxMHz
    {
        public UInt32 Number;           // The processor number. 
        public UInt32 MaxMhz;           // The maximum specified clock frequency of the system processor, in MHz. 
        public UInt32 CurrentMhz;       // The processor clock frequency, in MHz. This number is the maximum specified processor clock frequency multiplied by the current processor throttle. 
        public UInt32 MhzLimit;         // The limit on the processor clock frequency, in MHz. This number is the maximum specified processor clock frequency multiplied by the current processor thermal throttle limit. 
        public UInt32 MaxIdleState;     // The maximum idle state of this processor. 
        public UInt32 CurrentIdleState; // The current idle state of this processor. 
    }

    internal class CPUPowerWrapper
    {
        //Wrapper for CallNtPowerInformation
       internal static ArrayList GetAllProcessorPowerInfo()
        {
            const Int32 processorSpeedInformation = 11; //Used in call to CallNtPowerInformation 
            ArrayList CPUspeeds = new ArrayList();
            PROCESSOR_POWER_INFORMATION oneppi = new PROCESSOR_POWER_INFORMATION();
            uint ProcessorSpeedArraySize = (UInt32)Environment.ProcessorCount * (UInt32)Marshal.SizeOf(oneppi);
            IntPtr CPUInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(PROCESSOR_POWER_INFORMATION)) * Environment.ProcessorCount);
            uint retval = Win32.CallNtPowerInformation(
                    processorSpeedInformation,
                    (IntPtr)null,
                    (UInt32)0,
                    CPUInfo,
                    ProcessorSpeedArraySize);
            if (retval == 0)
            {
                IntPtr currentptr = ((IntPtr)CPUInfo);

                for (int i = 0; i < Environment.ProcessorCount; i++)
                {
                    PROCESSOR_POWER_INFORMATION ppi = (PROCESSOR_POWER_INFORMATION)Marshal.PtrToStructure(currentptr, typeof(PROCESSOR_POWER_INFORMATION));
                    currentptr = (IntPtr)((int)currentptr + Marshal.SizeOf(oneppi));
                    CPUspeeds.Add(ppi);
                }
            }
            Marshal.FreeCoTaskMem(CPUInfo);
            return CPUspeeds;
        }

        // Returns the Processor Spped information for the current processor
        internal static PROCESSOR_POWER_INFORMATION GetCurrentPowerInfoforProcessor()
        {
            PROCESSOR_POWER_INFORMATION ppi = new PROCESSOR_POWER_INFORMATION();
            ArrayList currentCPUspeeds;
            currentCPUspeeds = GetAllProcessorPowerInfo();
            uint currentCPUID = Win32.GetCurrentProcessorNumber();
            ppi = (PROCESSOR_POWER_INFORMATION) currentCPUspeeds[(int)currentCPUID];
            return ppi;
        }
    }


    public class MyStopwatch
    {
        private const long TicksPerMillisecond = 10000;
        private const long TicksPerSecond = TicksPerMillisecond * 1000;
        private const long CyclesPerMHz = 10;
        private const int OSVista = 6;
        
        private long elapsed;
        
        private long elapsedCPU;
        private long startTimeStamp;
        private long startCPUcycles;
        private long elapsedCPUThisPeriod;
        private uint timerStartThreadHandle;
        private UInt32 timerStartCPUID;
        private UInt32 timerStartCurrentMHz;
        private uint timerEndThreadHandle;
        private UInt32 timerEndCPUID;
        private UInt32 timerEndCurrentMHz;
        private UInt32 timerEndMaxMHz;

        //These flags indicate a state change between calls to Start() and Stop()
        private bool threadSwitchOccurred = false; //Thread switch is an error condition
        private bool cpuSwitchOccurred = false;     //Nice to know
        private bool cpuspeedChangeOccurred = false;//Nice to know when rdtsc does not report a constant tick rate
        
        private bool isRunning;
                
        // "Frequency" stores the frequency of the high-resolution performance counter, 
        // if one exists. Otherwise it will store TicksPerSecond. 
        // The frequency cannot change while the system is running,
        // so we only need to initialize it once. 
        public static readonly long Frequency;
        public static readonly bool IsHighResolution;
        
        // performance-counter frequency, in counts per ticks.
        // This can speed up conversion from high frequency performance-counter 
        // to ticks. 
        private static readonly double tickFrequency;
        
        //Statics added for QueryThreadCycleTime
        public static bool CPUTimeIsAvailable;
       

        static MyStopwatch()
        {
            bool succeeded = Win32.QueryPerformanceFrequency(out Frequency);
            
            if (!succeeded)
            {
                IsHighResolution = false;
                Frequency = TicksPerSecond;
                tickFrequency = 1;
            }
            else
            {
                IsHighResolution = true;
                tickFrequency = TicksPerSecond;
                tickFrequency /= Frequency;
            }
            // ThreadCycleTime is available beginning in Vista 
            if (Environment.OSVersion.Version.Major >= OSVista)
            {
                CPUTimeIsAvailable = true;
                }
            else
            {
                CPUTimeIsAvailable = false;
            }
        }
        
        public MyStopwatch()
        {
            Reset();
        }

        public void Start()
        {
            // Calling start on a running Stopwatch is a no-op.
            
            if (!isRunning)
            {
                 startTimeStamp = GetTimestamp();
                if (CPUTimeIsAvailable)
                {
                    //Remember the Thread ID, processor number & CurrentMHz
                    timerStartThreadHandle = Win32.GetCurrentThread();
                    timerStartCPUID = Win32.GetCurrentProcessorNumber();
                    PROCESSOR_POWER_INFORMATION thisppi;
                    thisppi = CPUPowerWrapper.GetCurrentPowerInfoforProcessor();
                    timerStartCurrentMHz = thisppi.CurrentMhz;
                    timerEndCurrentMHz = thisppi.CurrentMhz;
                    startCPUcycles = GetCPUCycles(); //CPU Cycles consumed by the thread up to this point
                    elapsedCPUThisPeriod = 0; 
                }
                isRunning = true;
            }
        }

        public static Stopwatch StartNew()
        {
            Stopwatch s = new Stopwatch();
            s.Start();
            return s;
        }

        public void Stop()
        {
            // Calling stop on a stopped Stopwatch is a no-op.
            if (isRunning)
            {
                long endTimeStamp = 0;
                endTimeStamp = GetTimestamp();

                if (CPUTimeIsAvailable)
                {
                    timerEndThreadHandle = Win32.GetCurrentThread();
                    long endCPUcycles = GetCPUCycles(); //Get CPU Cycles first
                    
                    if (timerEndThreadHandle != timerStartThreadHandle)
                    {
                        threadSwitchOccurred = true; //A thread switch is a problem, since CPU Cycles are accumulated
                        endCPUcycles = 0;            // at the thread level
                        startCPUcycles = 0;
                    }
                    else
                    {
                        if (endCPUcycles > startCPUcycles)
                        {
                            elapsedCPUThisPeriod = endCPUcycles - startCPUcycles;
                            elapsedCPU += elapsedCPUThisPeriod;
                        }
                        //How likely is it for the CPU time accumulator to wrap???
                        else
                        {
                            elapsedCPU = 0;
                            endCPUcycles = 0;
                            startCPUcycles = 0;
                        }
                    }
                }
                
                long elapsedThisPeriod = endTimeStamp - startTimeStamp;
                elapsed += elapsedThisPeriod;
                isRunning = false;
                if (elapsed < 0)
                {
                    // When measuring small time periods the StopWatch.Elapsed* 
                    // properties can return negative values.  This is due to 
                    // bugs in the basic input/output system (BIOS) or the hardware
                    // abstraction layer (HAL) on machines with variable-speed CPUs
                    // (e.g. Intel SpeedStep).

                    elapsed = 0;
                }
                
                if (CPUTimeIsAvailable && elapsedCPU > 0)
                {
                        timerEndCPUID = Win32.GetCurrentProcessorNumber();
                        if (timerEndCPUID != timerStartCPUID)
                        {
                            // Exposed as the FinishedOnDifferentProcessor property
                            // Stopwatch cannot detect context switches in general,
                            // but we do know at least one context switch occurred in this case
                            // 
                            cpuSwitchOccurred = true;  
                        }
                        PROCESSOR_POWER_INFORMATION thisppi = CPUPowerWrapper.GetCurrentPowerInfoforProcessor();
                        timerEndCurrentMHz = thisppi.CurrentMhz;
                        timerEndMaxMHz = thisppi.MaxMhz;
                        if (timerStartCurrentMHz != timerEndCurrentMHz) // CPU speed change detected
                        {
                            // Exposed as the PowerManagementChangeOccurred property
                            // so long as the rdtsc is constant across power management events,
                            // this is no cause for concern
                            cpuspeedChangeOccurred = true; 
                        }
                    }
                }
                
        }

        public void Reset()
        {
            elapsed = 0;
            isRunning = false;
            startTimeStamp = 0;

            //New fields added for CPU Cycles need to be reset
            elapsedCPU = 0;
            startCPUcycles = 0;
            threadSwitchOccurred = false;
            cpuSwitchOccurred = false;
            cpuspeedChangeOccurred = false; 
        }

        public bool IsRunning
        {
            get { return isRunning; }
        }

        public TimeSpan Elapsed
        {
            get { return new TimeSpan(GetElapsedDateTimeTicks()); }
        }

        // This routine returns elapsed CPU cycles as a TimeSpan
        // MaxMHz from the Stop method is used as the clock frequency to convert cycles into units of time
        //
        // On an older machine where the tsc does not run at a constant rate, this calculation
        // will lead to errors if 
        //      timerEndCurrentMhz <  timerEndMaxMHz OR
        //      timerEndCurrentMhz != timerStartCurrentMhz
        // No attempt is made to compensate for these errors

        public TimeSpan ElapsedCPU
        {
            get {
                if (CPUTimeIsAvailable && timerEndMaxMHz > 0)
                {
                    if (isRunning)
                    {
                        long elapsedCPUThisPeriod;
                        timerEndThreadHandle = Win32.GetCurrentThread();
                        long endCPUcycles = GetCPUCycles(); //Get CPU Cycles first

                        if (timerEndThreadHandle != timerStartThreadHandle)
                        {
                            threadSwitchOccurred = true; //A thread switch is a problem, since CPU Cycles are accumulated
                            endCPUcycles = 0;            // at the thread level
                            startCPUcycles = 0;
                        }
                        else
                        {
                            if (endCPUcycles > startCPUcycles)
                            {
                                elapsedCPUThisPeriod = endCPUcycles - startCPUcycles;
                                elapsedCPU += elapsedCPUThisPeriod;
                            }
                            //How likely is it for the CPU time accumulator to wrap???
                            else
                            {
                                elapsedCPU = 0;
                                endCPUcycles = 0;
                                startCPUcycles = 0;
                            }
                        }
                    }
                    double CPUns = elapsedCPU / timerEndMaxMHz ; // CPU time in usecs
                    CPUns *= 10;                                 // ticks in 100 nsec units
                    long ticks = (long) CPUns;
                    return new TimeSpan(ticks); 
                }
                else {return new TimeSpan(0); }
            }
        }

        public long ElapsedCPUticks
        {
            get { return (long) elapsedCPU; }
        }

        public long ElapsedMilliseconds
        {
            get { return GetElapsedDateTimeTicks() / TicksPerMillisecond; }
        }

        public long ElapsedTicks
       {
            get { return GetRawElapsedTicks(); }
        }

        public bool ThreadContextSwitchOccurred
        {
            get { return threadSwitchOccurred; }
        }

        public bool FinishedOnDifferentProcessor
        {
            get { return cpuSwitchOccurred; }
        }

        public bool PowerManagementChangeOccurred
        {
            get { return cpuspeedChangeOccurred; }
        }

        public int CurrentCPUMHz
        {
            get {return (int)timerEndCurrentMHz;}
        }

        public int MaxCPUMHz
        {
            get {return (int) timerEndMaxMHz;}
        }

        public static long GetCPUCycles ()
        {
            ulong cpucyclesused = 0;
            if (CPUTimeIsAvailable)
            {
                uint TidHandle = Win32.GetCurrentThread();
                bool CPUTimersucceeded = Win32.QueryThreadCycleTime(TidHandle, out cpucyclesused);
                if (CPUTimersucceeded)
                {
                    return (long) cpucyclesused;
                }
                else
                {
                    CPUTimeIsAvailable = false;
                    cpucyclesused = 0;
                }
            }
            else
            {
                CPUTimeIsAvailable = false;
                cpucyclesused = 0;
            }
            return (long) cpucyclesused;
        }

        public static long GetTimestamp()
        {
            if (IsHighResolution)
            {
                long timestamp = 0;
                Win32.QueryPerformanceCounter(out timestamp);
                return timestamp;
            }
            else
            {
                return DateTime.UtcNow.Ticks;
            }
        }

        // Get the elapsed ticks.        
        
        private long GetRawElapsedTicks()
        {
            long timeElapsed = elapsed;

            if (isRunning)
            {
                // If the StopWatch is running, add elapsed time since
                // the Stopwatch is started last time. 
                long currentTimeStamp = GetTimestamp();
                
                long elapsedUntilNow = currentTimeStamp - startTimeStamp;
                timeElapsed += elapsedUntilNow;
             }
            return timeElapsed;
        }

        // Get the elapsed cycles.        

        private long GetRawElapsedCycles()
        {
            long timeElapsed = elapsed;

            if (isRunning)
            {
                // If the StopWatch is running, add elapsed CPU cycles since
                // the Stopwatch is started last time. 
                long elapsedCPUUntilNow;

                long currentCPUcycles = GetCPUCycles();

                if (currentCPUcycles > startCPUcycles)
                {
                    elapsedCPUUntilNow = currentCPUcycles - startCPUcycles;
                    elapsedCPU += elapsedCPUUntilNow;
                }
                //How likely is it for the CPU time accumulator to wrap???
                else
                {
                }

            }
            return elapsedCPU;
        }


        // Get the elapsed ticks.        
        private long GetElapsedDateTimeTicks()
        {
            long rawTicks = GetRawElapsedTicks();
            if (IsHighResolution)
            {
                // convert high resolution perf counter to DateTime ticks
                double dticks = rawTicks;
                dticks *= tickFrequency;
                return unchecked((long)dticks);
            }
            else
            {
                return rawTicks;
            }
        }

       
     }
}

