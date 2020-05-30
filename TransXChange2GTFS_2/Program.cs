using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using System.Linq;
using CsvHelper;
using System.Diagnostics;
using System.IO.Compression;
using System.Xml;
using System.Globalization;
// Reference to GTFS standard https://developers.google.com/transit/gtfs/reference/#agencytxt

namespace TransXChange2GTFS_2
{
    class Program
    {
        static Dictionary<string, NaptanStop> NaPTANStopsDictionary = new Dictionary<string, NaptanStop>();
        static List<NaptanStop> NaptanStops = new List<NaptanStop>();
        static List<Agency> AgencyList = new List<Agency>();
        static List<Route> RoutesList = new List<Route>();
        static List<Calendar> calendarList = new List<Calendar>();
        static List<CalendarException> calendarExceptionsList = new List<CalendarException>();
        static List<Trip> tripList = new List<Trip>();
        static List<StopTime> stopTimesList = new List<StopTime>();
        static List<GTFSNaptanStop> GTFSStopsList = new List<GTFSNaptanStop>();
        static List<List<InternalRoute>> AllServicesInternalRoutes = new List<List<InternalRoute>>();
        static List<InternalRoute> InternalRoutesList = new List<InternalRoute>();
        static List<String> routesFailingProcessing = new List<String>();
        static List<String> routesSuccessProcessing = new List<String>();
        static BankHolidayDates bankHolidayDates = new BankHolidayDates();
        static HashSet<string> StopsCheck = new HashSet<string>();
        static HashSet<string> processedRoutes = new HashSet<string>();

        static string inputpath;

        // default for open ended end date
        const string DEFAULT_END_DATE = "20200101";


        static void Main(string[] args)
        {
            bankHolidayDates.AllBankHolidays = new List<string>(new string[] { "20180101", "20180330", "20180402", "20180507", "20180528", "20180827", "20181225", "20181226" });
            bankHolidayDates.GoodFriday = "20180330";
            bankHolidayDates.LateSummerBankHolidayNotScotland = "20180827";
            bankHolidayDates.EasterMonday = "20180402";
            bankHolidayDates.MayDay = "20180507";
            bankHolidayDates.SpringBank = "20180528";
            bankHolidayDates.ChristmasDay = "20181225";
            bankHolidayDates.BoxingDay = "20181226";
            bankHolidayDates.NewYearsDay = "20180101";

            //Reading NAPTAN
            if (Directory.Exists("temp") == true)
            {
                Directory.Delete("temp", true);
            }
            Directory.CreateDirectory("temp");

            Console.WriteLine("Unzipping NaPTAN to a temporary folder.");
            ZipFile.ExtractToDirectory(@"Stops.zip", "temp");
            using (TextReader textReader = File.OpenText("temp/Stops.csv"))
            {
                CsvReader csvReader = new CsvReader(textReader, CultureInfo.InvariantCulture);
                csvReader.Configuration.Delimiter = ",";
                NaptanStops = csvReader.GetRecords<NaptanStop>().ToList();
            }

            Console.WriteLine("Reading NaPTAN and creating an ATCOcode keyed dictionary of NaPTAN stops.");
            foreach (NaptanStop naptanStop in NaptanStops)
            {
                NaPTANStopsDictionary.Add(naptanStop.ATCOCode, naptanStop);
            }

            if (args.Count() == 0)
            {
                inputpath = @"path to TransXChange file here if you don't supply an argument";
            }
            else
            {
                inputpath = args[0];
            }

            if (inputpath.EndsWith(".zip"))
            {
                Console.WriteLine("Unzipping TransXChange collection to a temporary folder.");
                ZipFile.ExtractToDirectory(inputpath, "temp");
                foreach (string filePath in Directory.EnumerateFiles(@"temp", "*.xml"))
                {
                    convertTransXChange2GTFS(filePath);
                }
                Directory.Delete("temp", true);
            }
            else
            {
                foreach (string filePath in Directory.EnumerateFiles(@"input", "*.xml"))
                {
                    convertTransXChange2GTFS(filePath);
                }
            }

            processInternalRoutesList();
            writeGTFS();
            writeReport();

        }

        static void convertTransXChange2GTFS(string filePath)
        {
            Console.WriteLine("Converting " + filePath);
            InternalRoutesList = new List<InternalRoute>();
            string XMLAsAString = File.ReadAllText(filePath, Encoding.UTF8);
            byte[] byteArray = Encoding.UTF8.GetBytes(XMLAsAString);
            MemoryStream stream = new MemoryStream(byteArray);
            XmlSerializer serializer = new XmlSerializer(typeof(TransXChange));
            TransXChange _txObject;
            try
            {
                _txObject = (TransXChange)serializer.Deserialize(stream);
            }
            catch
            {
                Console.WriteLine($"Couldn't convert {filePath}. This is most likely because the TransXChange file was not in the expected form. If this error is frequent you'll need to investigate it.");
                return;
            }
            // Creating a journey patterns object
            TransXChangeJourneyPatternSection[] journeyPatternsArray = _txObject.JourneyPatternSections;

            foreach (var journeyPattern in journeyPatternsArray)
            {
                var journeyPatternTimingLink = journeyPattern.JourneyPatternTimingLink;
            }

            List<int> daysCheck;
            List<string> stopArray;
            List<string> stopTimesArray;
            List<string> timeGapArray;
            // Timings
            TransXChangeVehicleJourney[] VehicleJourneys = _txObject.VehicleJourneys;
            foreach (TransXChangeVehicleJourney VehicleJourney in VehicleJourneys)
            {
                if (VehicleJourney.JourneyPatternRef == null)
                {
                    Console.WriteLine($"There's a problem. The journey with LineRef {VehicleJourney.LineRef} at {VehicleJourney.DepartureTime} does not have a JourneyPatternRef. Skipping.");
                    continue;
                }

                List<string> noServiceDays = new List<string> { };
                List<string> extraServiceDays = new List<string> { };

                string journeyPatternRef = VehicleJourney.JourneyPatternRef;

                // sometimes the vehicle journey operating profile is empty. The operating profile can be taken from the service operating profile instead in this case.
                TransXChangeVehicleJourneyOperatingProfile VehicleJourneyOperatingProfile;
                if (VehicleJourney.OperatingProfile == null)
                {
                    VehicleJourneyOperatingProfile = _txObject.Services.Service.OperatingProfile;
                }
                else
                {
                    VehicleJourneyOperatingProfile = VehicleJourney.OperatingProfile;
                }

                // skip if HolidaysOnly (TODO)
                if (VehicleJourneyOperatingProfile.RegularDayType.HolidaysOnly != null)
                {
                    Console.Error.WriteLine("skip (because holiday journey parsing isn't complete yet): " + journeyPatternRef);
                    continue;
                }

                // Which days of the week does the service run on?
                TransXChangeVehicleJourneyOperatingProfileRegularDayTypeDaysOfWeek daysOfWeekObject = VehicleJourneyOperatingProfile.RegularDayType.DaysOfWeek;
                if (daysOfWeekObject == null)
                {
                    // if DaysOfWeek not specified, default to mon-sun as per spec.
                    daysCheck = new List<int> { 1, 1, 1, 1, 1, 1, 1 };
                }
                else if (daysOfWeekObject.MondayToFriday != null)
                {
                    daysCheck = new List<int> { 1, 1, 1, 1, 1, 0, 0 };
                }
                else if (daysOfWeekObject.MondayToSaturday != null)
                {
                    daysCheck = new List<int> { 1, 1, 1, 1, 1, 1, 0 };
                }
                else if (daysOfWeekObject.MondayToSunday != null)
                {
                    daysCheck = new List<int> { 1, 1, 1, 1, 1, 1, 1 };
                }
                else if (daysOfWeekObject.Weekend != null)
                {
                    daysCheck = new List<int> { 0, 0, 0, 0, 0, 1, 1 };
                }
                else
                {
                    // specific pattern of days
                    daysCheck = new List<int> {
                            ObjectToInt(daysOfWeekObject.Monday),
                            ObjectToInt(daysOfWeekObject.Tuesday),
                            ObjectToInt(daysOfWeekObject.Wednesday),
                            ObjectToInt(daysOfWeekObject.Thursday),
                            ObjectToInt(daysOfWeekObject.Friday),
                            ObjectToInt(daysOfWeekObject.Saturday),
                            ObjectToInt(daysOfWeekObject.Sunday)
                        };
                    // if DaysOfWeek not specified (DaysOfWeek present, days not specified),
                    // default to mon-sun as per spec.
                    if (daysCheck.Sum() == 0)
                        daysCheck = new List<int> { 1, 1, 1, 1, 1, 1, 1 };
                }

                // Which bank holidays does the service NOT run on?
                if (VehicleJourneyOperatingProfile.BankHolidayOperation != null)
                {
                    TransXChangeVehicleJourneyOperatingProfileBankHolidayOperationDaysOfNonOperation bankHolidaysWithNoService = VehicleJourneyOperatingProfile.BankHolidayOperation.DaysOfNonOperation;
                    if (bankHolidaysWithNoService != null)
                    {
                        if (bankHolidaysWithNoService.AllBankHolidays != null)
                        {
                            foreach (string bankholidayDate in bankHolidayDates.AllBankHolidays)
                            {
                                noServiceDays.Add(bankholidayDate);
                            }
                        }
                        if (bankHolidaysWithNoService.NewYearsDay != null)
                        {
                            noServiceDays.Add(bankHolidayDates.NewYearsDay);
                        }
                        if (bankHolidaysWithNoService.GoodFriday != null)
                        {
                            noServiceDays.Add(bankHolidayDates.GoodFriday);
                        }
                        if (bankHolidaysWithNoService.EasterMonday != null)
                        {
                            noServiceDays.Add(bankHolidayDates.EasterMonday);
                        }
                        if (bankHolidaysWithNoService.MayDay != null)
                        {
                            noServiceDays.Add(bankHolidayDates.MayDay);
                        }
                        if (bankHolidaysWithNoService.SpringBank != null)
                        {
                            noServiceDays.Add(bankHolidayDates.SpringBank);
                        }
                        if (bankHolidaysWithNoService.LateSummerBankHolidayNotScotland != null)
                        {
                            noServiceDays.Add(bankHolidayDates.LateSummerBankHolidayNotScotland);
                        }
                        if (bankHolidaysWithNoService.ChristmasDay != null)
                        {
                            noServiceDays.Add(bankHolidayDates.ChristmasDay);
                        }
                        if (bankHolidaysWithNoService.BoxingDay != null)
                        {
                            noServiceDays.Add(bankHolidayDates.BoxingDay);
                        }

                    }
                }

                if (VehicleJourneyOperatingProfile.SpecialDaysOperation != null)
                {
                    if (VehicleJourneyOperatingProfile.SpecialDaysOperation.DaysOfNonOperation != null)
                    {
                        TransXChangeVehicleJourneyOperatingProfileSpecialDaysOperationDateRange[] specialDaysOperation = VehicleJourneyOperatingProfile.SpecialDaysOperation.DaysOfNonOperation;
                        foreach (TransXChangeVehicleJourneyOperatingProfileSpecialDaysOperationDateRange specialOperationPeriod in specialDaysOperation)
                        {
                            DateTime startDate = specialOperationPeriod.StartDate;
                            DateTime endDate = specialOperationPeriod.EndDate;
                            DateTime dateThreshold = new DateTime(2018, 12, 31);

                            DateTime noServiceCurrentDay = startDate;
                            while (DateTime.Compare(noServiceCurrentDay, endDate) <= 0 && DateTime.Compare(noServiceCurrentDay, dateThreshold) <= 0)
                            {
                                // New date where the service doesn't run
                                string newNonServiceEntry = noServiceCurrentDay.ToString("yyyyMMdd");
                                noServiceDays.Add(newNonServiceEntry);

                                // Add 1 day
                                noServiceCurrentDay = noServiceCurrentDay.AddDays(1);
                            }
                        }
                    }
                }

                string startingDate = _txObject.Services.Service.OperatingPeriod.StartDate.ToString("yyyyMMdd");
                string finishingDate = _txObject.Services.Service.OperatingPeriod.EndDate.ToString("yyyyMMdd");

                // set to default open ended end date
                if (finishingDate.Equals("00010101"))
                {
                    finishingDate = DEFAULT_END_DATE;
                }

                string calendarID = "cal_" + startingDate + "_" + finishingDate + "_";

                string direction;

                DateTime currentDepartureTime = VehicleJourney.DepartureTime;
                string currentDepartureTimeFormat = (currentDepartureTime.ToString("HH:mm"));

                string currentPattern = journeyPatternRef;

                direction = _txObject.Services.Service.StandardService.JourneyPattern.Where(x => x.id == currentPattern).FirstOrDefault().Direction;
                if (direction == "inbound")
                {
                    direction = "1";
                }
                else
                {
                    direction = "0";
                }

                // Create stop list and timing list for this vehicle journey
                List<NaptanStop> currentStopList = new List<NaptanStop>();
                List<DateTime> currentTimesList = new List<DateTime>();
                String journeyPatternSectionRef = _txObject.Services.Service.StandardService.JourneyPattern.Where(x => x.id == currentPattern).FirstOrDefault().JourneyPatternSectionRefs;
                // Timings
                TransXChangeJourneyPatternSection CurrentJourneyPattern = journeyPatternsArray.Where(x => x.id == journeyPatternSectionRef).FirstOrDefault();

                var TimingLinks = CurrentJourneyPattern.JourneyPatternTimingLink;
                stopArray = new List<string>();
                stopTimesArray = new List<string>();
                timeGapArray = new List<string>();
                if (TimingLinks != null)
                {
                    for (int i = 0; i < TimingLinks.Length; i++)
                    {
                        // Time between stops
                        string timegap = TimingLinks[i].RunTime;
                        timeGapArray.Add(timegap);

                        string from = TimingLinks[i].From.StopPointRef;
                        string to = TimingLinks[i].To.StopPointRef;

                        stopArray.Add(from);

                        // only add the "TO" stop for the last stop
                        if (i == (TimingLinks.Length - 1))
                        {
                            stopArray.Add(to);
                        }
                    }

                    // Go through stops
                    TimeSpan stopsTime = new TimeSpan();

                    for (int j = 0; j < stopArray.Count; j++)
                    {
                        // First stop, just get the stop and departure time
                        if (j == 0)
                        {
                            stopsTime = currentDepartureTime.TimeOfDay;
                            stopTimesArray.Add(stopsTime.ToString(@"hh\:mm\:ss"));
                        }
                        // For subsequent stops work out the time between stops
                        else
                        {
                            string timeGap = timeGapArray[j - 1].ToString();
                            TimeSpan ts = XmlConvert.ToTimeSpan(timeGap);
                            stopsTime = stopsTime.Add(ts);
                            stopTimesArray.Add(stopsTime.ToString(@"hh\:mm\:ss"));
                        }
                    }

                    InternalRoute newRoute = new InternalRoute();
                    newRoute.Service = _txObject.Services.Service.ServiceCode;
                    newRoute.Departure = currentDepartureTime;
                    newRoute.Pattern = journeyPatternRef;
                    newRoute.Calendar = calendarID;
                    newRoute.Days = daysCheck;
                    newRoute.ExtraServiceDates = extraServiceDays;
                    newRoute.NoServiceDates = noServiceDays;
                    newRoute.StartingDate = startingDate;
                    newRoute.EndDate = finishingDate;
                    newRoute.Direction = direction;
                    newRoute.Times = stopTimesArray;
                    newRoute.Stops = stopArray;

                    InternalRoutesList.Add(newRoute);
                }
            }

            // There are valid routes for this service
            if (InternalRoutesList.Count > 0)
            {
                routesSuccessProcessing.Add(_txObject.Services.Service.ServiceCode);
                // Creating agency object     
                // Currently we assume that each route only has one operator
                TransXChangeOperatorsOperator operatorDetails = _txObject.Operators.Operator;

                // Adding a new agency  
                Agency agency = new Agency();
                agency.agency_id = operatorDetails.NationalOperatorCode;
                agency.agency_name = operatorDetails.OperatorShortName;
                agency.agency_url = "https://www.google.com/search?q=" + operatorDetails.OperatorShortName; // google plus name of agency by default
                agency.agency_timezone = "Europe/London"; // Europe/London by default

                // Check whether this agency is contained within the list
                var agencyCheck = AgencyList.FirstOrDefault(x => x.agency_id == operatorDetails.NationalOperatorCode);
                if (agencyCheck == null)
                {
                    AgencyList.Add(agency);
                }

                // Adding a new route
                // Calculate mode
                string mode = null;
                if (_txObject.Services.Service.Mode == "bus")
                {
                    mode = "3"; // there are more modes, but you need to look them up.
                }
                else if (_txObject.Services.Service.Mode == "coach")
                {
                    mode = "3";
                }
                else if (_txObject.Services.Service.Mode == "ferry")
                {
                    mode = "4";
                }
                else if (_txObject.Services.Service.Mode == "tram")
                {
                    mode = "0"; // yes there are more modes, here's the tram one.
                }
                else if (_txObject.Services.Service.Mode == "underground")
                {
                    mode = "1"; // yes there are more modes, and here's the london underground one.
                }
                else if (_txObject.Services.Service.Mode == "rail" && _txObject.Operators.Operator.OperatorCode == "EAL")
                {
                    // The Emirates Airline in London is classed as rail. That's wrong. It's a cablecar.
                    mode = "5";
                }
                else if (_txObject.Services.Service.Mode == "rail")
                {
                    mode = "1";
                    // This is for the DLR in London which is classed as "rail" in TransXChange for no good reason.
                    // There's some debate about whether it should be 0 or 1, but if The Editor of CityMetric says it's more like 1, that's enough for me.
                }
                if (mode == null)
                {
                    Console.WriteLine($"Transport mode is a required field, but the parser doesn't understand the {_txObject.Services.Service.Mode} mode.");
                    Console.WriteLine($"We've guess that it's a bus.");
                    mode = "3";
//                    throw new Exception();
                }
                
                // avoid duplicate route entries.
                string routeId = _txObject.Services.Service.ServiceCode;
                if (processedRoutes.Contains(routeId))
                {
                    Console.Error.WriteLine("skipping duplicate route: " + routeId);
                }
                else
                {
                    string Description = "No Description Given";
                    if (_txObject.Services.Service.Description != null) {
                        Description = _txObject.Services.Service.Description.Trim();
                    }


                    processedRoutes.Add(routeId);
                    Route route = new Route();
                    route.route_short_name = _txObject.Services.Service.Lines.Line.LineName;
                    route.route_long_name = Description;
                    route.route_id = routeId;
                    route.agency_id = operatorDetails.NationalOperatorCode;
                    route.route_color = null;
                    route.route_desc = null;
                    route.route_text_color = null;
                    route.route_url = null;
                    route.route_type = mode;
                    RoutesList.Add(route);
                }
                TransXChangeAnnotatedStopPointRef[] arrayOfStops = _txObject.StopPoints;
                foreach (TransXChangeAnnotatedStopPointRef stop in arrayOfStops)
                {
                    NaptanStop naptanStop;
                    NaPTANStopsDictionary.TryGetValue(stop.StopPointRef, out naptanStop);
                    NaptanStop naptanStop_withoutlastdigit;
                    NaPTANStopsDictionary.TryGetValue(stop.StopPointRef.Substring(0, stop.StopPointRef.Length - 1), out naptanStop_withoutlastdigit);
                    if (naptanStop == null && naptanStop_withoutlastdigit == null)
                    {
                        Console.WriteLine(stop.StopPointRef + " was not found in the Stops.csv file. And no similar stop could be found. The final gtfs file would not be valid if it was retained and the stop has been skipped.");
                        continue;
                    }
                    else
                    {
                        if (naptanStop == null)
                        {
                            naptanStop = naptanStop_withoutlastdigit;
                            naptanStop.ATCOCode = stop.StopPointRef;
                        }
                        
                        // Check whether this stop is already contained within the list
                        if (StopsCheck.Contains(naptanStop.ATCOCode) == false)
                        {
                            GTFSNaptanStop GTFSnaptanStop = new GTFSNaptanStop();
                            GTFSnaptanStop.stop_id = naptanStop.ATCOCode;
                            GTFSnaptanStop.stop_code = naptanStop.NaptanCode;
                            GTFSnaptanStop.stop_name = naptanStop.CommonName;
                            GTFSnaptanStop.stop_lat = Math.Round(naptanStop.Latitude,6);
                            GTFSnaptanStop.stop_lon = Math.Round(naptanStop.Longitude,6);
                            GTFSnaptanStop.stop_url = "";
                            // need to extract this from naptan data.
                            // 300 = bus
                            // https://developers.google.com/transit/gtfs/reference/extended-route-types
                            //GTFSnaptanStop.vehicle_type = "3";
                            StopsCheck.Add(naptanStop.ATCOCode);
                            GTFSStopsList.Add(GTFSnaptanStop);
                        }
                    }
                }


                // create calendar.txt, calendar_dates.txt, trips.txt, stop_times.txt from InternalRoutesList
                InternalRoutesList = InternalRoutesList.OrderBy(x => x.Departure).ToList();
                AllServicesInternalRoutes.Add(InternalRoutesList);
            }
        }

        static void processInternalRoutesList()
        {
            InternalRoutesList = InternalRoutesList.OrderBy(x => x.Service).ThenBy(x => x.Departure).ToList();

            // ServiceIDs have to be unique, and we take care to make them so. But there are sometimes exceptions. So we have to check. We use this HashSet to do that.
            HashSet<string> TripIDs = new HashSet<string>();

            foreach (List<InternalRoute> InternalRoutesList in AllServicesInternalRoutes)
            {
                // populate the above 5 lists from internalrouteslist  - calendar exceptions need finishing
                int internalRouteIndex = 1;
                foreach (InternalRoute InternalRoute in InternalRoutesList)
                {

                    // Get list of trips
                    Trip newTrip = new Trip();
                    newTrip.route_id = InternalRoute.Service;

                    newTrip.service_id = InternalRoute.Calendar + InternalRoute.Service + "-" + internalRouteIndex;
                    // can avoid duplicate trips by using (cal_from_to_id) since
                    // there is 1 trip per calendar entry.
                    newTrip.trip_id = newTrip.service_id; //InternalRoute.Service + "-" + internalRouteIndex;
                    newTrip.trip_headsign = null;
                    newTrip.direction_id = InternalRoute.Direction;
                    newTrip.block_id = null;
                    newTrip.shape_id = null;

                    if (TripIDs.Contains(newTrip.trip_id))
                    {
                        continue;
                    }
                    tripList.Add(newTrip);
                    TripIDs.Add(newTrip.trip_id);


                    // List of calendar entries
                    Calendar newCalendar = new Calendar();
                    newCalendar.service_id = InternalRoute.Calendar + InternalRoute.Service + "-" + internalRouteIndex;
                    newCalendar.monday = InternalRoute.Days[0];
                    newCalendar.tuesday = InternalRoute.Days[1];
                    newCalendar.wednesday = InternalRoute.Days[2];
                    newCalendar.thursday = InternalRoute.Days[3];
                    newCalendar.friday = InternalRoute.Days[4];
                    newCalendar.saturday = InternalRoute.Days[5];
                    newCalendar.sunday = InternalRoute.Days[6];
                    newCalendar.start_date = InternalRoute.StartingDate;
                    newCalendar.end_date = InternalRoute.EndDate;
                    calendarList.Add(newCalendar);

                    // This export line is more complicated than it might at first seem sensible to be because of an understandable quirk in the GTFS format.
                    // Stop times are only given as a time of day, and not a datetime. This causes problems when a service runs over midnight.
                    // To fix this we express stop times on a service that started the previous day with times such as 24:12 instead of 00:12 and 25:20 instead of 01:20.
                    // I assume that no journey runs into a third day.

                    // List of stop times
                    bool JourneyStartedYesterdayFlag = false;
                    TimeSpan PreviousStopDepartureTime = new TimeSpan(0);

                    for (var j = 0; j < InternalRoute.Stops.Count; j++)
                    {
                        StopTime newStopTime = new StopTime();
                        newStopTime.trip_id = newTrip.trip_id; //InternalRoute.Service + "-" + internalRouteIndex;
                        newStopTime.stop_id = InternalRoute.Stops[j];
                        newStopTime.stop_sequence = j + 1;
                        newStopTime.stop_headsign = null;
                        newStopTime.pickup_type = null;
                        newStopTime.drop_off_type = null;
                        newStopTime.shape_dist_traveled = null;

                        TimeSpan DepartureTimeAsTimeSpan = TimeSpan.ParseExact(InternalRoute.Times[j], @"hh\:mm\:ss", null);
                        if (DepartureTimeAsTimeSpan < PreviousStopDepartureTime)
                        {
                            JourneyStartedYesterdayFlag = true;
                        }
                        if (JourneyStartedYesterdayFlag == true)
                        {
                            TimeSpan UpdatedDepartureTimeAsTimeSpan = DepartureTimeAsTimeSpan.Add(new TimeSpan(24, 0, 0));
                            newStopTime.arrival_time = Math.Round(UpdatedDepartureTimeAsTimeSpan.TotalHours, 0).ToString() + UpdatedDepartureTimeAsTimeSpan.ToString(@"hh\:mm\:ss").Substring(2, 6);
                            newStopTime.departure_time = Math.Round(UpdatedDepartureTimeAsTimeSpan.TotalHours, 0).ToString() + UpdatedDepartureTimeAsTimeSpan.ToString(@"hh\:mm\:ss").Substring(2, 6);
                        }
                        else
                        {
                            newStopTime.arrival_time = InternalRoute.Times[j];
                            newStopTime.departure_time = InternalRoute.Times[j];
                        }

                        if (StopsCheck.Contains(newStopTime.stop_id))
                        {
                            stopTimesList.Add(newStopTime);
                        }
                        else
                        {
                            Console.WriteLine($"The trip {newStopTime.trip_id} contains a stop with id {newStopTime.stop_id} for which we have no information. It will not be written as this would create an invalid GTFS file. If this happens regularly you may need to update the NaPTAN stops file.");
                        }

                        PreviousStopDepartureTime = TimeSpan.ParseExact(InternalRoute.Times[j], @"hh\:mm\:ss", null);
                    }

                    // Add days with no service to exceptions
                    for (var j = 0; j < InternalRoute.NoServiceDates.Count; j++)
                    {
                        CalendarException newCalendarException = new CalendarException();
                        newCalendarException.service_id = InternalRoute.Calendar + InternalRoute.Service + "-" + internalRouteIndex;
                        newCalendarException.date = InternalRoute.NoServiceDates[j];
                        newCalendarException.exception_type = "2";
                        calendarExceptionsList.Add(newCalendarException);
                    }

                    // Add days with extra service to exceptions
                    for (var j = 0; j < InternalRoute.ExtraServiceDates.Count; j++)
                    {
                        CalendarException newCalendarException = new CalendarException();
                        newCalendarException.service_id = InternalRoute.Calendar + InternalRoute.Service + "-" + internalRouteIndex;
                        newCalendarException.date = null;
                        newCalendarException.exception_type = "1";
                        calendarExceptionsList.Add(newCalendarException);
                    }

                    internalRouteIndex++;

                }
            }
        }

        static void writeGTFS()
        {
            Console.WriteLine("Writing agency.txt");
            // write GTFS txts.
            // agency.txt, calendar.txt, calendar_dates.txt, routes.txt, stop_times.txt, stops.txt, trips.txt
            if (Directory.Exists("output") == false)
            {
                Directory.CreateDirectory("output");
            }

            TextWriter agencyTextWriter = File.CreateText(@"output/agency.txt");
            CsvWriter agencyCSVwriter = new CsvWriter(agencyTextWriter, CultureInfo.InvariantCulture);
            agencyCSVwriter.WriteRecords(AgencyList);
            agencyTextWriter.Dispose();
            agencyCSVwriter.Dispose();

            Console.WriteLine("Writing stops.txt");
            TextWriter stopsTextWriter = File.CreateText(@"output/stops.txt");
            CsvWriter stopsCSVwriter = new CsvWriter(stopsTextWriter, CultureInfo.InvariantCulture);
            stopsCSVwriter.WriteRecords(GTFSStopsList);
            stopsTextWriter.Dispose();
            stopsCSVwriter.Dispose();

            Console.WriteLine("Writing routes.txt");
            TextWriter routesTextWriter = File.CreateText(@"output/routes.txt");
            CsvWriter routesCSVwriter = new CsvWriter(routesTextWriter, CultureInfo.InvariantCulture);
            routesCSVwriter.WriteRecords(RoutesList);
            routesTextWriter.Dispose();
            routesCSVwriter.Dispose();

            Console.WriteLine("Writing trips.txt");
            TextWriter tripsTextWriter = File.CreateText(@"output/trips.txt");
            CsvWriter tripsCSVwriter = new CsvWriter(tripsTextWriter, CultureInfo.InvariantCulture);
            tripsCSVwriter.WriteRecords(tripList);
            tripsTextWriter.Dispose();
            tripsCSVwriter.Dispose();

            Console.WriteLine("Writing calendar.txt");
            TextWriter calendarTextWriter = File.CreateText(@"output/calendar.txt");
            CsvWriter calendarCSVwriter = new CsvWriter(calendarTextWriter, CultureInfo.InvariantCulture);
            calendarCSVwriter.WriteRecords(calendarList);
            calendarTextWriter.Dispose();
            calendarCSVwriter.Dispose();

            Console.WriteLine("Writing stop_times.txt");
            TextWriter stopTimeTextWriter = File.CreateText(@"output/stop_times.txt");
            CsvWriter stopTimeCSVwriter = new CsvWriter(stopTimeTextWriter, CultureInfo.InvariantCulture);
            stopTimeCSVwriter.WriteRecords(stopTimesList);
            stopTimeTextWriter.Dispose();
            stopTimeCSVwriter.Dispose();

            Console.WriteLine("Writing calendar_dates.txt");
            TextWriter calendarDatesTextWriter = File.CreateText(@"output/calendar_dates.txt");
            CsvWriter calendarDatesCSVwriter = new CsvWriter(calendarDatesTextWriter, CultureInfo.InvariantCulture);
            calendarDatesCSVwriter.WriteRecords(calendarExceptionsList);
            calendarDatesTextWriter.Dispose();
            calendarDatesCSVwriter.Dispose();

            Console.WriteLine("Creating a valid GTFS .zip file.");
            if (File.Exists($"{Path.GetFileNameWithoutExtension(inputpath)}_GTFS.zip"))
            {
                File.Delete($"{Path.GetFileNameWithoutExtension(inputpath)}_GTFS.zip");
            }
            ZipFile.CreateFromDirectory("output", $"{Path.GetFileNameWithoutExtension(inputpath)}_GTFS.zip", CompressionLevel.Optimal, false, Encoding.UTF8);
        }

        // Creates a text file showing a summary of results
        static void writeReport()
        {
            int totalRoutesProcessed = routesSuccessProcessing.Count() + routesFailingProcessing.Count();
            string text = "Total routes processed: " + totalRoutesProcessed + "\r\nRoutes processed successfully: " + routesSuccessProcessing.Count() + "\r\nRoutes failing processing: " + routesFailingProcessing.Count() + "\r\nFailed routes:\r\n" + String.Join("\r\n", routesFailingProcessing.ToArray());
            System.IO.File.WriteAllText(@"output/report.txt", text);
        }

        static int ObjectToInt(object input)
        {
            if (input != null)
            {
                return 1;
            }
            else
            {
                return 0;
            }
        }
    }
    // DESCRIBES EACH TRIP
    public class InternalRoute
    {
        public String Service { get; set; }
        public DateTime Departure { get; set; }
        public string Pattern { get; set; }
        public string Calendar { get; set; }
        public List<int> Days { get; set; }
        public List<string> ExtraServiceDates { get; set; }
        public List<string> NoServiceDates { get; set; }
        public string StartingDate { get; set; }
        public string EndDate { get; set; }
        public string Direction { get; set; }
        public List<string> Times { get; set; }
        public List<string> Stops { get; set; }
    }

    // LIST OF GB BANK HOLIDAYS https://www.gov.uk/bank-holidays.json
    public class BankHolidayDates
    {
        public List<string> AllBankHolidays { get; set; }
        public string GoodFriday { get; set; }
        public string LateSummerBankHolidayNotScotland { get; set; }
        public string EasterMonday { get; set; }
        public string MayDay { get; set; }
        public string SpringBank { get; set; }
        public string NewYearsDay { get; set; }
        public string ChristmasDay { get; set; }
        public string BoxingDay { get; set; }
    }

    // A LIST OF THESE CALENDAR OBJECTS CREATE THE GTFS calendar.txt file
    public class Calendar
    {
        public string service_id { get; set; }
        public int monday { get; set; }
        public int tuesday { get; set; }
        public int wednesday { get; set; }
        public int thursday { get; set; }
        public int friday { get; set; }
        public int saturday { get; set; }
        public int sunday { get; set; }
        public string start_date { get; set; }
        public string end_date { get; set; }
    }

    // A LIST OF THESE CALENDAR EXCEPTIONS CREATES THE GTFS  calendar_dates.txt file
    public class CalendarException
    {
        public string service_id { get; set; }
        public string date { get; set; }
        public string exception_type { get; set; }
    }

    // A LIST OF THESE TRIPS CREATES THE GTFS trips.txt file.
    public class Trip
    {
        public string route_id { get; set; }
        public string service_id { get; set; }
        public string trip_id { get; set; }
        public string trip_headsign { get; set; }
        public string direction_id { get; set; }
        public string block_id { get; set; }
        public string shape_id { get; set; }
    }

    // A LIST OF THESE STOPTIMES CREATES THE GTFS stop_times.txt file
    public class StopTime
    {
        public string trip_id { get; set; }
        public string arrival_time { get; set; }
        public string departure_time { get; set; }
        public string stop_id { get; set; }
        public int stop_sequence { get; set; }
        public string stop_headsign { get; set; }
        public string pickup_type { get; set; }
        public string drop_off_type { get; set; }
        public string shape_dist_traveled { get; set; }
    }

    //A LIST OF THESE NAPTANSTOPS CREATES THE GTFS stops.txt file
    public class GTFSNaptanStop
    {
        public string stop_id { get; set; }
        public string stop_code { get; set; }
        public string stop_name { get; set; }
        public decimal stop_lat { get; set; }
        public decimal stop_lon { get; set; }
        public string stop_url { get; set; }
        //public string vehicle_type { get; set; }
    }

    public class NaptanStop
    {
        public string ATCOCode { get; set; }
        public string NaptanCode { get; set; }
        public string CommonName { get; set; }
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
    }

    // A LIST OF THESE ROUTES CREATES THE GTFS routes.txt file.
    public class Route
    {
        public string route_id { get; set; }
        public string agency_id { get; set; }
        public string route_short_name { get; set; }
        public string route_long_name { get; set; }
        public string route_desc { get; set; }
        public string route_type { get; set; }
        public string route_url { get; set; }
        public string route_color { get; set; }
        public string route_text_color { get; set; }
    }

    // A LIST OF THESE AGENCIES CREATES THE GTFS agencies.txt file.
    public class Agency
    {
        public string agency_id { get; set; }
        public string agency_name { get; set; }
        public string agency_url { get; set; }
        public string agency_timezone { get; set; }
    }

    // This is auto-generated from the largest TransXChange file in the Traveline dataset.
    // NOTE: Generated code may require at least .NET Framework 4.5 or .NET Core/Standard 2.0.
    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    [System.Xml.Serialization.XmlRootAttribute(Namespace = "http://www.transxchange.org.uk/", IsNullable = false)]
    public partial class TransXChange
    {

        private TransXChangeAnnotatedStopPointRef[] stopPointsField;

        private TransXChangeRouteSection[] routeSectionsField;

        private TransXChangeRoute[] routesField;

        private TransXChangeJourneyPatternSection[] journeyPatternSectionsField;

        private TransXChangeOperators operatorsField;

        private TransXChangeServices servicesField;

        private TransXChangeVehicleJourney[] vehicleJourneysField;

        private System.DateTime creationDateTimeField;

        private System.DateTime modificationDateTimeField;

        private string modificationField;

        private byte revisionNumberField;

        private string fileNameField;

        private decimal schemaVersionField;

        private bool registrationDocumentField;

        /// <remarks/>
        [System.Xml.Serialization.XmlArrayItemAttribute("AnnotatedStopPointRef", IsNullable = false)]
        public TransXChangeAnnotatedStopPointRef[] StopPoints
        {
            get
            {
                return this.stopPointsField;
            }
            set
            {
                this.stopPointsField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlArrayItemAttribute("RouteSection", IsNullable = false)]
        public TransXChangeRouteSection[] RouteSections
        {
            get
            {
                return this.routeSectionsField;
            }
            set
            {
                this.routeSectionsField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlArrayItemAttribute("Route", IsNullable = false)]
        public TransXChangeRoute[] Routes
        {
            get
            {
                return this.routesField;
            }
            set
            {
                this.routesField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlArrayItemAttribute("JourneyPatternSection", IsNullable = false)]
        public TransXChangeJourneyPatternSection[] JourneyPatternSections
        {
            get
            {
                return this.journeyPatternSectionsField;
            }
            set
            {
                this.journeyPatternSectionsField = value;
            }
        }

        /// <remarks/>
        public TransXChangeOperators Operators
        {
            get
            {
                return this.operatorsField;
            }
            set
            {
                this.operatorsField = value;
            }
        }

        /// <remarks/>
        public TransXChangeServices Services
        {
            get
            {
                return this.servicesField;
            }
            set
            {
                this.servicesField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlArrayItemAttribute("VehicleJourney", IsNullable = false)]
        public TransXChangeVehicleJourney[] VehicleJourneys
        {
            get
            {
                return this.vehicleJourneysField;
            }
            set
            {
                this.vehicleJourneysField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public System.DateTime CreationDateTime
        {
            get
            {
                return this.creationDateTimeField;
            }
            set
            {
                this.creationDateTimeField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public System.DateTime ModificationDateTime
        {
            get
            {
                return this.modificationDateTimeField;
            }
            set
            {
                this.modificationDateTimeField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string Modification
        {
            get
            {
                return this.modificationField;
            }
            set
            {
                this.modificationField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public byte RevisionNumber
        {
            get
            {
                return this.revisionNumberField;
            }
            set
            {
                this.revisionNumberField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string FileName
        {
            get
            {
                return this.fileNameField;
            }
            set
            {
                this.fileNameField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public decimal SchemaVersion
        {
            get
            {
                return this.schemaVersionField;
            }
            set
            {
                this.schemaVersionField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public bool RegistrationDocument
        {
            get
            {
                return this.registrationDocumentField;
            }
            set
            {
                this.registrationDocumentField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeAnnotatedStopPointRef
    {

        private string stopPointRefField;

        private string commonNameField;

        private string localityNameField;

        private string localityQualifierField;

        /// <remarks/>
        public string StopPointRef
        {
            get
            {
                return this.stopPointRefField;
            }
            set
            {
                this.stopPointRefField = value;
            }
        }

        /// <remarks/>
        public string CommonName
        {
            get
            {
                return this.commonNameField;
            }
            set
            {
                this.commonNameField = value;
            }
        }

        /// <remarks/>
        public string LocalityName
        {
            get
            {
                return this.localityNameField;
            }
            set
            {
                this.localityNameField = value;
            }
        }

        /// <remarks/>
        public string LocalityQualifier
        {
            get
            {
                return this.localityQualifierField;
            }
            set
            {
                this.localityQualifierField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeRouteSection
    {

        private TransXChangeRouteSectionRouteLink[] routeLinkField;

        private string idField;

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute("RouteLink")]
        public TransXChangeRouteSectionRouteLink[] RouteLink
        {
            get
            {
                return this.routeLinkField;
            }
            set
            {
                this.routeLinkField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string id
        {
            get
            {
                return this.idField;
            }
            set
            {
                this.idField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeRouteSectionRouteLink
    {

        private TransXChangeRouteSectionRouteLinkFrom fromField;

        private TransXChangeRouteSectionRouteLinkTO toField;

        private ushort distanceField;

        private bool distanceFieldSpecified;

        private string directionField;

        private string idField;

        /// <remarks/>
        public TransXChangeRouteSectionRouteLinkFrom From
        {
            get
            {
                return this.fromField;
            }
            set
            {
                this.fromField = value;
            }
        }

        /// <remarks/>
        public TransXChangeRouteSectionRouteLinkTO To
        {
            get
            {
                return this.toField;
            }
            set
            {
                this.toField = value;
            }
        }

        /// <remarks/>
        public ushort Distance
        {
            get
            {
                return this.distanceField;
            }
            set
            {
                this.distanceField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlIgnoreAttribute()]
        public bool DistanceSpecified
        {
            get
            {
                return this.distanceFieldSpecified;
            }
            set
            {
                this.distanceFieldSpecified = value;
            }
        }

        /// <remarks/>
        public string Direction
        {
            get
            {
                return this.directionField;
            }
            set
            {
                this.directionField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string id
        {
            get
            {
                return this.idField;
            }
            set
            {
                this.idField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeRouteSectionRouteLinkFrom
    {

        private string stopPointRefField;

        /// <remarks/>
        public string StopPointRef
        {
            get
            {
                return this.stopPointRefField;
            }
            set
            {
                this.stopPointRefField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeRouteSectionRouteLinkTO
    {

        private string stopPointRefField;

        /// <remarks/>
        public string StopPointRef
        {
            get
            {
                return this.stopPointRefField;
            }
            set
            {
                this.stopPointRefField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeRoute
    {

        private string privateCodeField;

        private string descriptionField;

        private string routeSectionRefField;

        private string idField;

        /// <remarks/>
        public string PrivateCode
        {
            get
            {
                return this.privateCodeField;
            }
            set
            {
                this.privateCodeField = value;
            }
        }

        /// <remarks/>
        public string Description
        {
            get
            {
                return this.descriptionField;
            }
            set
            {
                this.descriptionField = value;
            }
        }

        /// <remarks/>
        public string RouteSectionRef
        {
            get
            {
                return this.routeSectionRefField;
            }
            set
            {
                this.routeSectionRefField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string id
        {
            get
            {
                return this.idField;
            }
            set
            {
                this.idField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeJourneyPatternSection
    {

        private TransXChangeJourneyPatternSectionJourneyPatternTimingLink[] journeyPatternTimingLinkField;

        private string idField;

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute("JourneyPatternTimingLink")]
        public TransXChangeJourneyPatternSectionJourneyPatternTimingLink[] JourneyPatternTimingLink
        {
            get
            {
                return this.journeyPatternTimingLinkField;
            }
            set
            {
                this.journeyPatternTimingLinkField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string id
        {
            get
            {
                return this.idField;
            }
            set
            {
                this.idField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeJourneyPatternSectionJourneyPatternTimingLink
    {

        private TransXChangeJourneyPatternSectionJourneyPatternTimingLinkFrom fromField;

        private TransXChangeJourneyPatternSectionJourneyPatternTimingLinkTO toField;

        private string routeLinkRefField;

        private string runTimeField;

        private string idField;

        /// <remarks/>
        public TransXChangeJourneyPatternSectionJourneyPatternTimingLinkFrom From
        {
            get
            {
                return this.fromField;
            }
            set
            {
                this.fromField = value;
            }
        }

        /// <remarks/>
        public TransXChangeJourneyPatternSectionJourneyPatternTimingLinkTO To
        {
            get
            {
                return this.toField;
            }
            set
            {
                this.toField = value;
            }
        }

        /// <remarks/>
        public string RouteLinkRef
        {
            get
            {
                return this.routeLinkRefField;
            }
            set
            {
                this.routeLinkRefField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute(DataType = "duration")]
        public string RunTime
        {
            get
            {
                return this.runTimeField;
            }
            set
            {
                this.runTimeField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string id
        {
            get
            {
                return this.idField;
            }
            set
            {
                this.idField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeJourneyPatternSectionJourneyPatternTimingLinkFrom
    {

        private string activityField;

        private string stopPointRefField;

        private string timingStatusField;

        private byte sequenceNumberField;

        /// <remarks/>
        public string Activity
        {
            get
            {
                return this.activityField;
            }
            set
            {
                this.activityField = value;
            }
        }

        /// <remarks/>
        public string StopPointRef
        {
            get
            {
                return this.stopPointRefField;
            }
            set
            {
                this.stopPointRefField = value;
            }
        }

        /// <remarks/>
        public string TimingStatus
        {
            get
            {
                return this.timingStatusField;
            }
            set
            {
                this.timingStatusField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public byte SequenceNumber
        {
            get
            {
                return this.sequenceNumberField;
            }
            set
            {
                this.sequenceNumberField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeJourneyPatternSectionJourneyPatternTimingLinkTO
    {

        private string waitTimeField;

        private string activityField;

        private string stopPointRefField;

        private string timingStatusField;

        private byte sequenceNumberField;

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute(DataType = "duration")]
        public string WaitTime
        {
            get
            {
                return this.waitTimeField;
            }
            set
            {
                this.waitTimeField = value;
            }
        }

        /// <remarks/>
        public string Activity
        {
            get
            {
                return this.activityField;
            }
            set
            {
                this.activityField = value;
            }
        }

        /// <remarks/>
        public string StopPointRef
        {
            get
            {
                return this.stopPointRefField;
            }
            set
            {
                this.stopPointRefField = value;
            }
        }

        /// <remarks/>
        public string TimingStatus
        {
            get
            {
                return this.timingStatusField;
            }
            set
            {
                this.timingStatusField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public byte SequenceNumber
        {
            get
            {
                return this.sequenceNumberField;
            }
            set
            {
                this.sequenceNumberField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeOperators
    {

        private TransXChangeOperatorsOperator operatorField;

        /// <remarks/>
        public TransXChangeOperatorsOperator Operator
        {
            get
            {
                return this.operatorField;
            }
            set
            {
                this.operatorField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeOperatorsOperator
    {

        private string nationalOperatorCodeField;

        private string operatorCodeField;

        private string operatorShortNameField;

        private string operatorNameOnLicenceField;

        private string tradingNameField;

        private string idField;

        /// <remarks/>
        public string NationalOperatorCode
        {
            get
            {
                return this.nationalOperatorCodeField;
            }
            set
            {
                this.nationalOperatorCodeField = value;
            }
        }

        /// <remarks/>
        public string OperatorCode
        {
            get
            {
                return this.operatorCodeField;
            }
            set
            {
                this.operatorCodeField = value;
            }
        }

        /// <remarks/>
        public string OperatorShortName
        {
            get
            {
                return this.operatorShortNameField;
            }
            set
            {
                this.operatorShortNameField = value;
            }
        }

        /// <remarks/>
        public string OperatorNameOnLicence
        {
            get
            {
                return this.operatorNameOnLicenceField;
            }
            set
            {
                this.operatorNameOnLicenceField = value;
            }
        }

        /// <remarks/>
        public string TradingName
        {
            get
            {
                return this.tradingNameField;
            }
            set
            {
                this.tradingNameField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string id
        {
            get
            {
                return this.idField;
            }
            set
            {
                this.idField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServices
    {

        private TransXChangeServicesService serviceField;

        /// <remarks/>
        public TransXChangeServicesService Service
        {
            get
            {
                return this.serviceField;
            }
            set
            {
                this.serviceField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesService
    {

        private string serviceCodeField;

        private string privateCodeField;

        private TransXChangeServicesServiceLines linesField;

        private TransXChangeServicesServiceOperatingPeriod operatingPeriodField;

        //private TransXChangeServicesServiceOperatingProfile operatingProfileField;
        private TransXChangeVehicleJourneyOperatingProfile operatingProfileField;

        private string registeredOperatorRefField;

        private TransXChangeServicesServiceStopRequirements stopRequirementsField;

        private string modeField;

        private string descriptionField;

        private TransXChangeServicesServiceStandardService standardServiceField;

        /// <remarks/>
        public string ServiceCode
        {
            get
            {
                return this.serviceCodeField;
            }
            set
            {
                this.serviceCodeField = value;
            }
        }

        /// <remarks/>
        public string PrivateCode
        {
            get
            {
                return this.privateCodeField;
            }
            set
            {
                this.privateCodeField = value;
            }
        }

        /// <remarks/>
        public TransXChangeServicesServiceLines Lines
        {
            get
            {
                return this.linesField;
            }
            set
            {
                this.linesField = value;
            }
        }

        /// <remarks/>
        public TransXChangeServicesServiceOperatingPeriod OperatingPeriod
        {
            get
            {
                return this.operatingPeriodField;
            }
            set
            {
                this.operatingPeriodField = value;
            }
        }

        /// <remarks/>
        //public TransXChangeServicesServiceOperatingProfile OperatingProfile
        public TransXChangeVehicleJourneyOperatingProfile OperatingProfile
        {
            get
            {
                return this.operatingProfileField;
            }
            set
            {
                this.operatingProfileField = value;
            }
        }

        /// <remarks/>
        public string RegisteredOperatorRef
        {
            get
            {
                return this.registeredOperatorRefField;
            }
            set
            {
                this.registeredOperatorRefField = value;
            }
        }

        /// <remarks/>
        public TransXChangeServicesServiceStopRequirements StopRequirements
        {
            get
            {
                return this.stopRequirementsField;
            }
            set
            {
                this.stopRequirementsField = value;
            }
        }

        /// <remarks/>
        public string Mode
        {
            get
            {
                return this.modeField;
            }
            set
            {
                this.modeField = value;
            }
        }

        /// <remarks/>
        public string Description
        {
            get
            {
                return this.descriptionField;
            }
            set
            {
                this.descriptionField = value;
            }
        }

        /// <remarks/>
        public TransXChangeServicesServiceStandardService StandardService
        {
            get
            {
                return this.standardServiceField;
            }
            set
            {
                this.standardServiceField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceLines
    {

        private TransXChangeServicesServiceLinesLine lineField;

        /// <remarks/>
        public TransXChangeServicesServiceLinesLine Line
        {
            get
            {
                return this.lineField;
            }
            set
            {
                this.lineField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceLinesLine
    {

        private string lineNameField;

        private string idField;

        /// <remarks/>
        public string LineName
        {
            get
            {
                return this.lineNameField;
            }
            set
            {
                this.lineNameField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string id
        {
            get
            {
                return this.idField;
            }
            set
            {
                this.idField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceOperatingPeriod
    {

        private System.DateTime startDateField;

        private System.DateTime endDateField;

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute(DataType = "date")]
        public System.DateTime StartDate
        {
            get
            {
                return this.startDateField;
            }
            set
            {
                this.startDateField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute(DataType = "date")]
        public System.DateTime EndDate
        {
            get
            {
                return this.endDateField;
            }
            set
            {
                this.endDateField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceOperatingProfile
    {

        private TransXChangeServicesServiceOperatingProfileRegularDayType regularDayTypeField;

        private TransXChangeServicesServiceOperatingProfileSpecialDaysOperation specialDaysOperationField;

        private TransXChangeServicesServiceOperatingProfileBankHolidayOperation bankHolidayOperationField;

        /// <remarks/>
        public TransXChangeServicesServiceOperatingProfileRegularDayType RegularDayType
        {
            get
            {
                return this.regularDayTypeField;
            }
            set
            {
                this.regularDayTypeField = value;
            }
        }

        /// <remarks/>
        public TransXChangeServicesServiceOperatingProfileSpecialDaysOperation SpecialDaysOperation
        {
            get
            {
                return this.specialDaysOperationField;
            }
            set
            {
                this.specialDaysOperationField = value;
            }
        }

        /// <remarks/>
        public TransXChangeServicesServiceOperatingProfileBankHolidayOperation BankHolidayOperation
        {
            get
            {
                return this.bankHolidayOperationField;
            }
            set
            {
                this.bankHolidayOperationField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceOperatingProfileRegularDayType
    {

        private TransXChangeServicesServiceOperatingProfileRegularDayTypeDaysOfWeek daysOfWeekField;

        /// <remarks/>
        public TransXChangeServicesServiceOperatingProfileRegularDayTypeDaysOfWeek DaysOfWeek
        {
            get
            {
                return this.daysOfWeekField;
            }
            set
            {
                this.daysOfWeekField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceOperatingProfileRegularDayTypeDaysOfWeek
    {

        private object mondayToSundayField;

        /// <remarks/>
        public object MondayToSunday
        {
            get
            {
                return this.mondayToSundayField;
            }
            set
            {
                this.mondayToSundayField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceOperatingProfileSpecialDaysOperation
    {

        private TransXChangeServicesServiceOperatingProfileSpecialDaysOperationDateRange[] daysOfNonOperationField;

        /// <remarks/>
        [System.Xml.Serialization.XmlArrayItemAttribute("DateRange", IsNullable = false)]
        public TransXChangeServicesServiceOperatingProfileSpecialDaysOperationDateRange[] DaysOfNonOperation
        {
            get
            {
                return this.daysOfNonOperationField;
            }
            set
            {
                this.daysOfNonOperationField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceOperatingProfileSpecialDaysOperationDateRange
    {

        private System.DateTime startDateField;

        private System.DateTime endDateField;

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute(DataType = "date")]
        public System.DateTime StartDate
        {
            get
            {
                return this.startDateField;
            }
            set
            {
                this.startDateField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute(DataType = "date")]
        public System.DateTime EndDate
        {
            get
            {
                return this.endDateField;
            }
            set
            {
                this.endDateField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceOperatingProfileBankHolidayOperation
    {

        private TransXChangeServicesServiceOperatingProfileBankHolidayOperationDaysOfOperation daysOfOperationField;

        private object daysOfNonOperationField;

        /// <remarks/>
        public TransXChangeServicesServiceOperatingProfileBankHolidayOperationDaysOfOperation DaysOfOperation
        {
            get
            {
                return this.daysOfOperationField;
            }
            set
            {
                this.daysOfOperationField = value;
            }
        }

        /// <remarks/>
        public object DaysOfNonOperation
        {
            get
            {
                return this.daysOfNonOperationField;
            }
            set
            {
                this.daysOfNonOperationField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceOperatingProfileBankHolidayOperationDaysOfOperation
    {

        private object goodFridayField;

        private object mayDayField;

        private object easterMondayField;

        private object springBankField;

        /// <remarks/>
        public object GoodFriday
        {
            get
            {
                return this.goodFridayField;
            }
            set
            {
                this.goodFridayField = value;
            }
        }

        /// <remarks/>
        public object MayDay
        {
            get
            {
                return this.mayDayField;
            }
            set
            {
                this.mayDayField = value;
            }
        }

        /// <remarks/>
        public object EasterMonday
        {
            get
            {
                return this.easterMondayField;
            }
            set
            {
                this.easterMondayField = value;
            }
        }

        /// <remarks/>
        public object SpringBank
        {
            get
            {
                return this.springBankField;
            }
            set
            {
                this.springBankField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceStopRequirements
    {

        private object noNewStopsRequiredField;

        /// <remarks/>
        public object NoNewStopsRequired
        {
            get
            {
                return this.noNewStopsRequiredField;
            }
            set
            {
                this.noNewStopsRequiredField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceStandardService
    {

        private string originField;

        private string destinationField;

        private TransXChangeServicesServiceStandardServiceJourneyPattern[] journeyPatternField;

        /// <remarks/>
        public string Origin
        {
            get
            {
                return this.originField;
            }
            set
            {
                this.originField = value;
            }
        }

        /// <remarks/>
        public string Destination
        {
            get
            {
                return this.destinationField;
            }
            set
            {
                this.destinationField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute("JourneyPattern")]
        public TransXChangeServicesServiceStandardServiceJourneyPattern[] JourneyPattern
        {
            get
            {
                return this.journeyPatternField;
            }
            set
            {
                this.journeyPatternField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceStandardServiceJourneyPattern
    {

        private string directionField;

        private TransXChangeServicesServiceStandardServiceJourneyPatternOperational operationalField;

        private string routeRefField;

        private string journeyPatternSectionRefsField;

        private string idField;

        /// <remarks/>
        public string Direction
        {
            get
            {
                return this.directionField;
            }
            set
            {
                this.directionField = value;
            }
        }

        /// <remarks/>
        public TransXChangeServicesServiceStandardServiceJourneyPatternOperational Operational
        {
            get
            {
                return this.operationalField;
            }
            set
            {
                this.operationalField = value;
            }
        }

        /// <remarks/>
        public string RouteRef
        {
            get
            {
                return this.routeRefField;
            }
            set
            {
                this.routeRefField = value;
            }
        }

        /// <remarks/>
        public string JourneyPatternSectionRefs
        {
            get
            {
                return this.journeyPatternSectionRefsField;
            }
            set
            {
                this.journeyPatternSectionRefsField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string id
        {
            get
            {
                return this.idField;
            }
            set
            {
                this.idField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceStandardServiceJourneyPatternOperational
    {

        private TransXChangeServicesServiceStandardServiceJourneyPatternOperationalVehicleType vehicleTypeField;

        /// <remarks/>
        public TransXChangeServicesServiceStandardServiceJourneyPatternOperationalVehicleType VehicleType
        {
            get
            {
                return this.vehicleTypeField;
            }
            set
            {
                this.vehicleTypeField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeServicesServiceStandardServiceJourneyPatternOperationalVehicleType
    {

        private string vehicleTypeCodeField;

        private string descriptionField;

        /// <remarks/>
        public string VehicleTypeCode
        {
            get
            {
                return this.vehicleTypeCodeField;
            }
            set
            {
                this.vehicleTypeCodeField = value;
            }
        }

        /// <remarks/>
        public string Description
        {
            get
            {
                return this.descriptionField;
            }
            set
            {
                this.descriptionField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourney
    {

        private string privateCodeField;

        private TransXChangeVehicleJourneyOperational operationalField;

        private TransXChangeVehicleJourneyOperatingProfile operatingProfileField;

        private string vehicleJourneyCodeField;

        private string serviceRefField;

        private string lineRefField;

        private string journeyPatternRefField;

        private System.DateTime departureTimeField;

        /// <remarks/>
        public string PrivateCode
        {
            get
            {
                return this.privateCodeField;
            }
            set
            {
                this.privateCodeField = value;
            }
        }

        /// <remarks/>
        public TransXChangeVehicleJourneyOperational Operational
        {
            get
            {
                return this.operationalField;
            }
            set
            {
                this.operationalField = value;
            }
        }

        /// <remarks/>
        public TransXChangeVehicleJourneyOperatingProfile OperatingProfile
        {
            get
            {
                return this.operatingProfileField;
            }
            set
            {
                this.operatingProfileField = value;
            }
        }

        /// <remarks/>
        public string VehicleJourneyCode
        {
            get
            {
                return this.vehicleJourneyCodeField;
            }
            set
            {
                this.vehicleJourneyCodeField = value;
            }
        }

        /// <remarks/>
        public string ServiceRef
        {
            get
            {
                return this.serviceRefField;
            }
            set
            {
                this.serviceRefField = value;
            }
        }

        /// <remarks/>
        public string LineRef
        {
            get
            {
                return this.lineRefField;
            }
            set
            {
                this.lineRefField = value;
            }
        }

        /// <remarks/>
        public string JourneyPatternRef
        {
            get
            {
                return this.journeyPatternRefField;
            }
            set
            {
                this.journeyPatternRefField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute(DataType = "time")]
        public System.DateTime DepartureTime
        {
            get
            {
                return this.departureTimeField;
            }
            set
            {
                this.departureTimeField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperational
    {

        private TransXChangeVehicleJourneyOperationalVehicleType vehicleTypeField;

        /// <remarks/>
        public TransXChangeVehicleJourneyOperationalVehicleType VehicleType
        {
            get
            {
                return this.vehicleTypeField;
            }
            set
            {
                this.vehicleTypeField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperationalVehicleType
    {

        private string vehicleTypeCodeField;

        private string descriptionField;

        /// <remarks/>
        public string VehicleTypeCode
        {
            get
            {
                return this.vehicleTypeCodeField;
            }
            set
            {
                this.vehicleTypeCodeField = value;
            }
        }

        /// <remarks/>
        public string Description
        {
            get
            {
                return this.descriptionField;
            }
            set
            {
                this.descriptionField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperatingProfile
    {

        private TransXChangeVehicleJourneyOperatingProfileRegularDayType regularDayTypeField;

        private TransXChangeVehicleJourneyOperatingProfileSpecialDaysOperation specialDaysOperationField;

        private TransXChangeVehicleJourneyOperatingProfileBankHolidayOperation bankHolidayOperationField;

        /// <remarks/>
        public TransXChangeVehicleJourneyOperatingProfileRegularDayType RegularDayType
        {
            get
            {
                return this.regularDayTypeField;
            }
            set
            {
                this.regularDayTypeField = value;
            }
        }

        /// <remarks/>
        public TransXChangeVehicleJourneyOperatingProfileSpecialDaysOperation SpecialDaysOperation
        {
            get
            {
                return this.specialDaysOperationField;
            }
            set
            {
                this.specialDaysOperationField = value;
            }
        }

        /// <remarks/>
        public TransXChangeVehicleJourneyOperatingProfileBankHolidayOperation BankHolidayOperation
        {
            get
            {
                return this.bankHolidayOperationField;
            }
            set
            {
                this.bankHolidayOperationField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperatingProfileRegularDayType
    {

        private TransXChangeVehicleJourneyOperatingProfileRegularDayTypeDaysOfWeek daysOfWeekField;
        private TransXChangeVehicleJourneyOperatingProfileRegularDayTypeHolidaysOnly holidaysOnlyField;

        /// <remarks/>
        public TransXChangeVehicleJourneyOperatingProfileRegularDayTypeDaysOfWeek DaysOfWeek
        {
            get
            {
                return this.daysOfWeekField;
            }
            set
            {
                this.daysOfWeekField = value;
            }
        }

        public TransXChangeVehicleJourneyOperatingProfileRegularDayTypeHolidaysOnly HolidaysOnly
        {
            get
            {
                return this.holidaysOnlyField;
            }
            set
            {
                this.holidaysOnlyField = value;
            }
        }

    }

    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperatingProfileRegularDayTypeHolidaysOnly
    {
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperatingProfileRegularDayTypeDaysOfWeek
    {

        private object mondayField;

        private object tuesdayField;

        private object wednesdayField;

        private object thursdayField;

        private object sundayField;

        private object saturdayField;

        private object fridayField;

        private object mondayToFridayField;

        private object mondayToSaturdayField;

        private object mondayToSundayField;

        private object weekendField;

        /// <remarks/>
        public object Monday
        {
            get
            {
                return this.mondayField;
            }
            set
            {
                this.mondayField = value;
            }
        }

        /// <remarks/>
        public object Tuesday
        {
            get
            {
                return this.tuesdayField;
            }
            set
            {
                this.tuesdayField = value;
            }
        }

        /// <remarks/>
        public object Wednesday
        {
            get
            {
                return this.wednesdayField;
            }
            set
            {
                this.wednesdayField = value;
            }
        }

        /// <remarks/>
        public object Thursday
        {
            get
            {
                return this.thursdayField;
            }
            set
            {
                this.thursdayField = value;
            }
        }

        /// <remarks/>
        public object Sunday
        {
            get
            {
                return this.sundayField;
            }
            set
            {
                this.sundayField = value;
            }
        }

        /// <remarks/>
        public object Saturday
        {
            get
            {
                return this.saturdayField;
            }
            set
            {
                this.saturdayField = value;
            }
        }

        /// <remarks/>
        public object Friday
        {
            get
            {
                return this.fridayField;
            }
            set
            {
                this.fridayField = value;
            }
        }

        public object MondayToFriday
        {
            get
            {
                return this.mondayToFridayField;
            }
            set
            {
                this.mondayToFridayField = value;
            }
        }

        public object MondayToSaturday
        {
            get
            {
                return this.mondayToSaturdayField;
            }
            set
            {
                this.mondayToSaturdayField = value;
            }
        }

        public object MondayToSunday
        {
            get
            {
                return this.mondayToSundayField;
            }
            set
            {
                this.mondayToSundayField = value;
            }
        }

        public object Weekend
        {
            get
            {
                return this.weekendField;
            }
            set
            {
                this.weekendField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperatingProfileSpecialDaysOperation
    {

        private TransXChangeVehicleJourneyOperatingProfileSpecialDaysOperationDateRange[] daysOfNonOperationField;

        /// <remarks/>
        [System.Xml.Serialization.XmlArrayItemAttribute("DateRange", IsNullable = false)]
        public TransXChangeVehicleJourneyOperatingProfileSpecialDaysOperationDateRange[] DaysOfNonOperation
        {
            get
            {
                return this.daysOfNonOperationField;
            }
            set
            {
                this.daysOfNonOperationField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperatingProfileSpecialDaysOperationDateRange
    {

        private System.DateTime startDateField;

        private System.DateTime endDateField;

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute(DataType = "date")]
        public System.DateTime StartDate
        {
            get
            {
                return this.startDateField;
            }
            set
            {
                this.startDateField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute(DataType = "date")]
        public System.DateTime EndDate
        {
            get
            {
                return this.endDateField;
            }
            set
            {
                this.endDateField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperatingProfileBankHolidayOperation
    {

        private TransXChangeVehicleJourneyOperatingProfileBankHolidayOperationDaysOfOperation daysOfOperationField;

        private TransXChangeVehicleJourneyOperatingProfileBankHolidayOperationDaysOfNonOperation daysOfNonOperationField;

        /// <remarks/>
        public TransXChangeVehicleJourneyOperatingProfileBankHolidayOperationDaysOfOperation DaysOfOperation
        {
            get
            {
                return this.daysOfOperationField;
            }
            set
            {
                this.daysOfOperationField = value;
            }
        }

        /// <remarks/>
        public TransXChangeVehicleJourneyOperatingProfileBankHolidayOperationDaysOfNonOperation DaysOfNonOperation
        {
            get
            {
                return this.daysOfNonOperationField;
            }
            set
            {
                this.daysOfNonOperationField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperatingProfileBankHolidayOperationDaysOfOperation
    {

        private object goodFridayField;

        private object mayDayField;

        private object easterMondayField;

        private object springBankField;

        private object lateSummerBankHolidayNotScotlandBankField;

        /// <remarks/>
        public object LateSummerBankHolidayNotScotland
        {
            get
            {
                return this.lateSummerBankHolidayNotScotlandBankField;
            }
            set
            {
                this.lateSummerBankHolidayNotScotlandBankField = value;
            }
        }

        /// <remarks/>
        public object GoodFriday
        {
            get
            {
                return this.goodFridayField;
            }
            set
            {
                this.goodFridayField = value;
            }
        }

        /// <remarks/>
        public object MayDay
        {
            get
            {
                return this.mayDayField;
            }
            set
            {
                this.mayDayField = value;
            }
        }

        /// <remarks/>
        public object EasterMonday
        {
            get
            {
                return this.easterMondayField;
            }
            set
            {
                this.easterMondayField = value;
            }
        }

        /// <remarks/>
        public object SpringBank
        {
            get
            {
                return this.springBankField;
            }
            set
            {
                this.springBankField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.transxchange.org.uk/")]
    public partial class TransXChangeVehicleJourneyOperatingProfileBankHolidayOperationDaysOfNonOperation
    {

        private object goodFridayField;

        private object mayDayField;

        private object easterMondayField;

        private object springBankField;

        private object allBankHolidaysField;

        private object lateSummerBankHolidayNotScotlandBankField;

        private object christmasDayField;

        private object boxingDayField;

        private object newYearsDayField;

        /// <remarks/>
        public object NewYearsDay
        {
            get
            {
                return this.newYearsDayField;
            }
            set
            {
                this.newYearsDayField = value;
            }
        }

        /// <remarks/>
        public object ChristmasDay
        {
            get
            {
                return this.christmasDayField;
            }
            set
            {
                this.christmasDayField = value;
            }
        }

        /// <remarks/>
        public object BoxingDay
        {
            get
            {
                return this.boxingDayField;
            }
            set
            {
                this.boxingDayField = value;
            }
        }

        /// <remarks/>
        public object LateSummerBankHolidayNotScotland
        {
            get
            {
                return this.lateSummerBankHolidayNotScotlandBankField;
            }
            set
            {
                this.lateSummerBankHolidayNotScotlandBankField = value;
            }
        }

        /// <remarks/>
        public object GoodFriday
        {
            get
            {
                return this.goodFridayField;
            }
            set
            {
                this.goodFridayField = value;
            }
        }

        /// <remarks/>
        public object MayDay
        {
            get
            {
                return this.mayDayField;
            }
            set
            {
                this.mayDayField = value;
            }
        }

        /// <remarks/>
        public object EasterMonday
        {
            get
            {
                return this.easterMondayField;
            }
            set
            {
                this.easterMondayField = value;
            }
        }

        /// <remarks/>
        public object SpringBank
        {
            get
            {
                return this.springBankField;
            }
            set
            {
                this.springBankField = value;
            }
        }

        /// <remarks/>
        public object AllBankHolidays
        {
            get
            {
                return this.allBankHolidaysField;
            }
            set
            {
                this.allBankHolidaysField = value;
            }
        }
    }

}