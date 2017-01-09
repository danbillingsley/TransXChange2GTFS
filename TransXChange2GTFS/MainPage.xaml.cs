using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace TransXChange2GTFS
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    /// 

    public sealed partial class MainPage : Page
    {
        // GLOBAL variables
        string calendarID = "";
        Dictionary<string, string> busstopDictionary = new Dictionary<string, string>(); 

        // Each of these lists will have lines added to it to become on of the files that will be outputted
        List<string> stopsArray;
        List<string> agencyArray;
        List<string> routesArray;
        List<string> stop_timesArray;
        List<string> tripsArray;
        List<string> calendarArray;
        List<string> calendar_datesArray;

        // Batch list populated from the contents of single XML files.
        List<string> batchStopsArray;
        List<string> batchAgencyArray;
        List<string> batchRoutesArray;
        List<string> batchStop_timesArray;
        List<string> batchTripsArray;
        List<string> batchCalendarArray;
        List<string> batchCalendar_datesArray;

        public MainPage()
        {
            this.InitializeComponent();            
        }

        private void createBusstopDictionary()
        {
            // don't repopulate if it's already populated
            if (busstopDictionary.Count == 0)
            {
                List<string> busStopsArray = File.ReadAllLines(@"Assets\stops.txt").ToList();
                foreach (var stopline in busStopsArray)
                {
                    string busStopID = stopline.Split(new string[] { "," }, StringSplitOptions.None)[0];
                   // busstopDictionary.Add(busStopID, stopline);
                    busstopDictionary[busStopID] = stopline;
                }
            }
        }

        private async void saveLocationButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            FolderPicker savePicker = new FolderPicker();
            savePicker.ViewMode = PickerViewMode.List;
            savePicker.FileTypeFilter.Add(".txt");
            StorageFolder saveFolder = await savePicker.PickSingleFolderAsync();
            if (saveFolder != null)
            {
                // Application now has read/write access to all contents in the picked folder
                // (including other sub-folder contents)
                Windows.Storage.AccessCache.StorageApplicationPermissions.
                FutureAccessList.AddOrReplace("PickedFolderToken", saveFolder);
            
                saveProgress.IsActive = true;
                saveProgressText.Text = "Saving agency.txt";
                // Remove possible duplicates in agency list before saving
                batchAgencyArray = batchAgencyArray.Distinct().ToList();
                await Task.Run(() =>
                {
                    File.WriteAllLines(saveFolder.Path + "\\agency.txt", batchAgencyArray);
                });
                saveProgressText.Text = "Saving routes.txt";
                await Task.Run(() =>
                {
                    File.WriteAllLines(saveFolder.Path + "\\routes.txt", batchRoutesArray);
                });
                saveProgressText.Text = "Saving stop_times.txt";
                await Task.Run(() =>
                {
                    File.WriteAllLines(saveFolder.Path + "\\stop_times.txt", batchStop_timesArray);
                });
                    saveProgressText.Text = "Saving trips.txt";
                await Task.Run(() =>
                {
                    File.WriteAllLines(saveFolder.Path + "\\trips.txt", batchTripsArray);
                });
                    saveProgressText.Text = "Saving calendar.txt";
                await Task.Run(() =>
                {
                    File.WriteAllLines(saveFolder.Path + "\\calendar.txt", batchCalendarArray);
                 });
                saveProgressText.Text = "Saving calendar_dates.txt";
                await Task.Run(() =>
                {
                    File.WriteAllLines(saveFolder.Path + "\\calendar_dates.txt", batchCalendar_datesArray);
                });
                saveProgressText.Text = "Saving stops.txt";
                // Remove any duplicate stops in the list
                batchStopsArray = batchStopsArray.Distinct().ToList();
                await Task.Run(() =>
                {
                    File.WriteAllLines(saveFolder.Path + "\\stops.txt", batchStopsArray);
                });
                saveProgressText.Text = "";
                saveProgress.IsActive = false;
            
            }
        }

            private async void pickFolderButton_Tapped(object sender, TappedRoutedEventArgs e)
        {

            // select an xml file
            FolderPicker folderPicker = new FolderPicker();
            folderPicker.ViewMode = PickerViewMode.List;
            folderPicker.FileTypeFilter.Add(".xml");
            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                // Reset any existing content
                resetBatchArrayFunction();

                // Application now has read/write access to all contents in the picked folder
                // (including other sub-folder contents)
                Windows.Storage.AccessCache.StorageApplicationPermissions.
                FutureAccessList.AddOrReplace("PickedFolderToken", folder);
                Debug.Write(folder.Name);
                // Get the files in the current folder.
                IReadOnlyList<StorageFile> filesInFolder = await folder.GetFilesAsync();
               
                // Set Minimum to 1 to represent the first file being copied.
                progressBar.Minimum = 1;
                // Set Maximum to the total number of files to copy.
                progressBar.Maximum = filesInFolder.Count;
                // Set the initial value of the ProgressBar.
                progressBar.Value = 0;

                saveContent.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                completionText.Text = "";
                progressIndicator.Visibility = Windows.UI.Xaml.Visibility.Visible;

                // Iterate through each file in the folder
                foreach (StorageFile f in filesInFolder)
                {
                    // Update the progress bar increment
                    progressBar.Value = progressBar.Value + 1;
                    // If the file type is xml
                    if (f.FileType == ".xml")
                    { 
                        resetArrayFunction();
                        clearUI();
                        // Update progress text
                        fileReading.Text = "Currently reading: " + f.Name;
                        await readAndParseXML(f);
                    }
                }

                // Hide progress bar and reset text content
                progressIndicator.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                fileReading.Text = "";
                
                // Display save button
                saveContent.Visibility = Windows.UI.Xaml.Visibility.Visible;
                completionText.Text = "Finished Processing";
            }

        }

        private async Task readAndParseXML(StorageFile file)
        {
            Progress.IsActive = true;
            ProgressText.Text = "Loading XML.";

            // load XMl, convert to JSON, make available as serviceObject (used for all future parsing)
            XDocument loadedXML = XDocument.Load(await file.OpenStreamForReadAsync());
            string jsonString = JsonConvert.SerializeXNode(loadedXML);
            var serviceObject = JsonConvert.DeserializeObject<dynamic>(jsonString);
            var serviceCode = serviceObject["TransXChange"]["Services"]["Service"]["ServiceCode"];

            writeXMLAndJSON(loadedXML, jsonString);
            ProgressText.Text = "Loading bus stops.";
            await Task.Run(() =>
            {
                createBusstopDictionary();
            });            
            await Task.Run(() =>
            {
                writeServiceNameDescriptionAndCode(serviceObject);                
            });
            ProgressText.Text = "Creating routes.";
            writeOperatorDetails(serviceObject);
            await Task.Run(() =>
            {
                writeAgencyAndRoutesText(serviceObject);
            });
            ProgressText.Text = "Listing stops.";
            await Task.Run(() =>
            {
                writeStopsArray(serviceObject);
            });
            ProgressText.Text = "Calculating days of operation.";
            JObject journeyPatternsObject = createJourneyPatternsObject(serviceObject);
            JArray patternAndTimesArray = createPatternAndTimeArray(serviceObject, journeyPatternsObject);
            await Task.Run(() =>
            {
                createCalendarTripsDatesAndTimesFromPatternAndTimes(patternAndTimesArray, serviceCode.Value);
            });

            Progress.IsActive = false;
            ProgressText.Text = "";
        }

        private void resetArrayFunction()
        {
            // Each of these lists will have lines added to it to become on of the files that will be outputted
            stopsArray = new List<string>(new string[] { "stop_id,stop_code,stop_name,stop_lat,stop_lon,stop_url,vehicle_type" });
            agencyArray = new List<string>(new string[] { "agency_id,agency_name,agency_url,agency_timezone" });
            routesArray = new List<string>(new string[] { "route_id,agency_id,route_short_name,route_long_name,route_desc,route_type,route_url,route_color,route_text_color" });
            stop_timesArray = new List<string>(new string[] { "trip_id, arrival_time, departure_time, stop_id, stop_sequence, stop_headsign, pickup_type, drop_off_time, shape_dist_traveled" });
            tripsArray = new List<string>(new string[] { "route_id, service_id, trip_id, trip_headsign, direction_id, block_id, shape_id" });
            calendarArray = new List<string>(new string[] { "service_id, monday, tuesday, wednesday, thursday, friday, saturday, sunday, start_date, end_date" });
            calendar_datesArray = new List<string>(new string[] { "service_id, date, exception_type" });
        }

        private void resetBatchArrayFunction()
        {
            // Each of these lists will have lines added to it to become on of the files that will be outputted
            batchStopsArray = new List<string>(new string[] { "stop_id,stop_code,stop_name,stop_lat,stop_lon,stop_url,vehicle_type" });
            batchAgencyArray = new List<string>(new string[] { "agency_id,agency_name,agency_url,agency_timezone" });
            batchRoutesArray = new List<string>(new string[] { "route_id,agency_id,route_short_name,route_long_name,route_desc,route_type,route_url,route_color,route_text_color" });
            batchStop_timesArray = new List<string>(new string[] { "trip_id, arrival_time, departure_time, stop_id, stop_sequence, stop_headsign, pickup_type, drop_off_time, shape_dist_traveled" });
            batchTripsArray = new List<string>(new string[] { "route_id, service_id, trip_id, trip_headsign, direction_id, block_id, shape_id" });
            batchCalendarArray = new List<string>(new string[] { "service_id, monday, tuesday, wednesday, thursday, friday, saturday, sunday, start_date, end_date" });
            batchCalendar_datesArray = new List<string>(new string[] { "service_id, date, exception_type" });
        }

        private JArray createPatternAndTimeArray(dynamic serviceObject, JObject journeyPatternsObject)
        {
            // Dates of Uk holiday dates
            string[] days = new string[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            JArray bankHolidays2016 = new JArray("20160101", "20160325", "20160328", "20160502", "20160530", "20160829", "20161226", "20161227");


            JObject holidayDaysArray = JObject.Parse(
                    @"{
                           'GoodFriday': {
                                'Date': '20160325',
                                'DayIndex': 4
                           },
                           'EasterMonday': {
                                'Date': '20160328',
                                'DayIndex': 0
                            }
                         }"
            );

            // Reset the content of the arrays
            JArray patternAndTimesArray = new JArray();
            JArray daysCheck = new JArray();
            JArray stopArray = new JArray();
            JArray timingLinkarray = new JArray();
            JArray stopTimesArray = new JArray();
            JArray timeGapArray = new JArray();

            // Timings
            JArray arrayOfTimings = new JArray(serviceObject["TransXChange"]["VehicleJourneys"]["VehicleJourney"]);

            for (int i = 0; i < arrayOfTimings.Count; i++)
            {
                JArray noServiceDays = new JArray();
                JArray extraServiceDays = new JArray();

                var journeyPatternRef = arrayOfTimings[i]["JourneyPatternRef"];
                var daysOfWeekObject = arrayOfTimings[i]["OperatingProfile"]["RegularDayType"]["DaysOfWeek"];

                // Checks the days that the service runs on. 
                daysCheck = new JArray(0, 0, 0, 0, 0, 0, 0);

                var startingDate = "20150901";
                var finishingDate = "20170101";
                // Direction
                string direction = "";

                if (daysOfWeekObject != null)
                {

                    for (int j = 0; j < days.Length; j++)
                    {

                        foreach (JProperty x in (JToken)arrayOfTimings[i]["OperatingProfile"]["RegularDayType"]["DaysOfWeek"])
                        {
                            string serviceDay = x.Name;
                            if (serviceDay == days[j].ToString())
                            {
                                daysCheck[j] = 1;
                            }

                        }
                    }

                    // If the service doesn't run on bank holidays then add these days to the list of non operating services.
                    if (arrayOfTimings[i]["OperatingProfile"]["BankHolidayOperation"]["DaysOfNonOperation"] != null)
                    {
                        for (int j = 0; j < bankHolidays2016.Count; j++)
                        {
                            noServiceDays.Add(bankHolidays2016[j]);
                        }
                    }
                    calendarID = "cal_";
                }

                foreach (JProperty x in (JToken)arrayOfTimings[i]["OperatingProfile"]["RegularDayType"])
                {
                    string operatingProfile = x.Name;

                    if (operatingProfile == "HolidaysOnly")
                    {

                        calendarID = "cal_HOL_";

                        if (arrayOfTimings[i]["Note"] != null)
                        {
                            // The extra holiday day is not explicitly stated, then search the list
                            if (arrayOfTimings[i]["Note"]["NoteText"].ToString() == "OtherPublicHoliday")
                            {

                                startingDate = arrayOfTimings[i]["OperatingProfile"]["SpecialDaysOperation"]["DaysOfOperation"]["DateRange"]["StartDate"].ToString().Replace("-", String.Empty);
                                finishingDate = arrayOfTimings[i]["OperatingProfile"]["SpecialDaysOperation"]["DaysOfOperation"]["DateRange"]["StartDate"].ToString().Replace("-", String.Empty);

                                DateTime extraDayFormat = new DateTime();
                                extraDayFormat = DateTime.ParseExact(startingDate, "yyyyMMdd", null);

                                string ExtraDay = extraDayFormat.ToString("dddd");

                                int extraDayIndex = Array.IndexOf(days, ExtraDay);

                                daysCheck[extraDayIndex] = 1;

                            }
                            // The holiday is stated, get the details from the holidays array. 
                            else
                            {
                                var holidayDay = arrayOfTimings[i]["Note"]["NoteText"].ToString();
                                startingDate = holidayDaysArray[holidayDay]["Date"].ToString();
                                finishingDate = holidayDaysArray[holidayDay]["Date"].ToString();

                                // Parse the extra day as an integer, needs to be converted to an string first. Then set the extra value in the days array  
                                int extraDayIndex = Int32.Parse(holidayDaysArray[holidayDay]["DayIndex"].ToString());
                                daysCheck[extraDayIndex] = 1;
                            }
                        }
                    }
                }

                DateTime currentDepartureTime = DateTime.ParseExact(arrayOfTimings[i]["DepartureTime"].ToString(), "HH:mm:ss", null);
                string currentDepartureTimeFormat = (currentDepartureTime.ToString("HH:mm"));

                dealWithSpecialDaysOfOperation(arrayOfTimings, i, noServiceDays, extraServiceDays);
                direction = calculateDirectionFromJourneyPatternsObject(journeyPatternsObject, journeyPatternRef);

                var currentPattern = journeyPatternRef;

                // Arrays for stops and times
                JArray currentStopList = new JArray();
                JArray currentTimesList = new JArray();

                // Timings
                timingLinkarray = new JArray(journeyPatternsObject[currentPattern.ToString()]["JourneyPatternTimingLink"]);

                stopArray = new JArray();
                stopTimesArray = new JArray();
                timeGapArray = new JArray();

                // More than one stop in the journey
                if (timingLinkarray[0].GetType().ToString().Contains("Array"))
                {
                    for (int j = 0; j < timingLinkarray[0].Count(); j++)
                    {

                        // Time between stops
                        var timegap = timingLinkarray[0][j]["RunTime"];
                        timeGapArray.Add(timegap);

                        String from = (timingLinkarray[0][j]["From"]["StopPointRef"]).ToString();
                        String to = (timingLinkarray[0][j]["To"]["StopPointRef"]).ToString();

                        // only add to if the last addition is not the same as this one. Only adds the from the first time
                        if (j == 0)
                        {
                            stopArray.Add(from);
                        }
                        stopArray.Add(to);

                    }
                }
                // Just two stops point to point
                else
                {
                    // Time between stops
                    var timegap = timingLinkarray[0]["RunTime"];
                    timeGapArray.Add(timegap);

                    String from = (timingLinkarray[0]["From"]["StopPointRef"]).ToString();
                    String to = (timingLinkarray[0]["To"]["StopPointRef"]).ToString();

                    stopArray.Add(from);
                    stopArray.Add(to);
                }

                DateTime stopsTime = new DateTime();

                for (int j = 0; j < stopArray.Count; j++)
                {
                    // First stop, just get the stop and departure time
                    if (j == 0)
                    {
                        // stopsTime = DateTime.ParseExact(departureTime, "HH:mm", null);
                        stopsTime = currentDepartureTime;
                        stopTimesArray.Add(stopsTime.ToString("HH:mm:ss"));
                    }
                    // For subsequent stops work out the time between stops
                    else
                    {
                        // Remove the leading and trailing sections of the time leaving only the amount of seconds to add on.
                        var timeGap = timeGapArray[j - 1].ToString();
                        // I've added the "M" -- not sure if that's safe
                        var cleanedTimeGap = timeGap.Split(new string[] { "PT" }, StringSplitOptions.None)[1].Replace("S", string.Empty).Replace("M", string.Empty);
                        int timeIncrease = int.Parse(cleanedTimeGap);
                        stopsTime = stopsTime.AddSeconds(timeIncrease);
                        stopTimesArray.Add(stopsTime.ToString("HH:mm:ss"));
                    }

                }

                JObject newRouteObject =
                new JObject(
                    new JProperty("Departure", currentDepartureTime),
                    new JProperty("Pattern", journeyPatternRef),
                    new JProperty("Calendar", calendarID),
                    new JProperty("Days", daysCheck),
                    new JProperty("ExtraServiceDates", extraServiceDays),
                    new JProperty("NoServiceDates", noServiceDays),
                    new JProperty("StartingDate", startingDate),
                    new JProperty("EndDate", finishingDate),
                    new JProperty("Direction", direction),
                    new JProperty("Times", stopTimesArray),
                    new JProperty("Stops", stopArray)
                 );

                patternAndTimesArray.Add(newRouteObject);
            }
            return patternAndTimesArray;
        }

        private async void writeStopsArray(dynamic serviceObject)
        {
            var arrayOfStops = serviceObject["TransXChange"]["StopPoints"]["AnnotatedStopPointRef"];

            List<string> arrayOfStopCodes = new List<string>();

            foreach (var stop in arrayOfStops)
            {
                string stopRef = stop["StopPointRef"].Value; //this lookup is really SLOW!! It takes up to 250ms?!
                arrayOfStopCodes.Add(stopRef);
            }

            foreach (var stop in arrayOfStopCodes)
            {
                try
                {
                    string stopInfo = busstopDictionary[stop];
                    stopsArray.Add(stopInfo);
                    batchStopsArray.Add(stopInfo);
                }
                catch(KeyNotFoundException)
                {
                    Debug.Write("Bus stop " + stop + " was not found in the stops.txt file.");
                }
            }

            // UI change needs pushing back to the UI thread
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    () =>
                    {
                        Stops.Text = listAsString(stopsArray);
                    });
        }

        private JObject createJourneyPatternsObject(dynamic serviceObject)
        {
            JObject journeyPatternsObject = new JObject();
            JArray journeyPatternsArray = new JArray(serviceObject["TransXChange"]["JourneyPatternSections"]["JourneyPatternSection"]);

            // More than one journey
            if (journeyPatternsArray.Type.ToString() == "Array")
            {
                for (int i = 0; i < journeyPatternsArray.Count; i++)
                {
                    string patternName = (journeyPatternsArray[i]["@id"]).ToString();
                    journeyPatternsObject.Add(patternName, journeyPatternsArray[i]);
                }
            }

            // Just one journey
            else
            {
                String patternName = (journeyPatternsArray["@id"]).ToString();
                journeyPatternsObject.Add(patternName, journeyPatternsArray);
            }
            return journeyPatternsObject;
        }

        private async void writeAgencyAndRoutesText(dynamic serviceObject)
        {
            var serviceName = serviceObject["TransXChange"]["Services"]["Service"]["Lines"]["Line"]["LineName"];
            var serviceDescription = serviceObject["TransXChange"]["Services"]["Service"]["Description"];
            var serviceCode = serviceObject["TransXChange"]["Services"]["Service"]["ServiceCode"];
            JArray operatorDetails = new JArray(serviceObject["TransXChange"]["Operators"]["Operator"]);
            var operatorID = operatorDetails[0]["@id"];

            for (int i = 0; i < operatorDetails.Count; i++)
            {
                agencyArray.Add((operatorDetails[i]["@id"] + "," + operatorDetails[i]["OperatorShortName"] + ",,").ToString());
                batchAgencyArray.Add((operatorDetails[i]["@id"] + "," + operatorDetails[i]["OperatorShortName"] + ",,").ToString());

            }

            routesArray.Add((serviceCode + "," + operatorID + "," + serviceName + "," + serviceDescription + ",,3,,,").ToString());
            batchRoutesArray.Add((serviceCode + "," + operatorID + "," + serviceName + "," + serviceDescription + ",,3,,,").ToString());

            // UI change needs pushing back to the UI thread
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    () =>
                    {
                        Agency.Text = listAsString(agencyArray);
                        Routes.Text = listAsString(routesArray);
                    });

        }

        private void writeOperatorDetails(dynamic serviceObject)
        {
            JArray operatorDetails = new JArray(serviceObject["TransXChange"]["Operators"]["Operator"]);
            var operatorID = operatorDetails[0]["@id"];
            var operatorName = operatorDetails[0]["OperatorShortName"];
        }

        private async void writeServiceNameDescriptionAndCode(dynamic serviceObject)
        {
            var serviceName = serviceObject["TransXChange"]["Services"]["Service"]["Lines"]["Line"]["LineName"];
            var serviceDescription = serviceObject["TransXChange"]["Services"]["Service"]["Description"];
            var serviceCode = serviceObject["TransXChange"]["Services"]["Service"]["ServiceCode"];

            // UI change needs pushing back to the UI thread
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    () =>
                    {
                        ServiceName.Text = serviceName.Value;
                        ServiceDescription.Text = serviceDescription.Value;
                    });

        }

        private void writeXMLAndJSON(XDocument loadedXML, string jsonString)
        {
            XMLHolder.Text = loadedXML.ToString().Substring(0, 1000); // only show the first 1000 characters in a textbox, for speed
            JSONHolder.Text = jsonString.ToString().Substring(0, 1000); // only show the first 1000 characters in a textbox, for speed
        }

        private string calculateDirectionFromJourneyPatternsObject(JObject journeyPatternsObject, JToken journeyPatternRef)
        {
            string direction = "";
            if (journeyPatternsObject[journeyPatternRef.ToString()]["JourneyPatternTimingLink"].GetType().ToString().Contains("Array"))
            {
                direction = journeyPatternsObject[journeyPatternRef.ToString()]["JourneyPatternTimingLink"][0]["Direction"].ToString();
            }
            // Just one direction.
            else
            {
                direction = journeyPatternsObject[journeyPatternRef.ToString()]["JourneyPatternTimingLink"]["Direction"].ToString();
            }

            if (direction == "outbound")
            {
                direction = "0";
            }
            if (direction == "inbound")
            {
                direction = "1";
            }
            return direction;
        }

        private void dealWithSpecialDaysOfOperation(JArray arrayOfTimings, int i, JArray noServiceDays, JArray extraServiceDays)
        {
            if (arrayOfTimings[i]["OperatingProfile"]["SpecialDaysOperation"] != null)
            {
                if (arrayOfTimings[i]["OperatingProfile"]["SpecialDaysOperation"]["DaysOfNonOperation"] != null)
                {
                    JArray specialDaysNonOperation = new JArray(arrayOfTimings[i]["OperatingProfile"]["SpecialDaysOperation"]["DaysOfNonOperation"]["DateRange"]);

                    // Multiple extra dates
                    if (specialDaysNonOperation[0].GetType().ToString().Contains("Array"))
                    {
                        for (int j = 0; j < specialDaysNonOperation[0].Count(); j++)
                        {

                            var noServiceStart = (specialDaysNonOperation[0][j]["StartDate"]).ToString().Replace("-", String.Empty);
                            var noServiceEnd = (specialDaysNonOperation[0][j]["EndDate"]).ToString().Replace("-", String.Empty);

                            DateTime noServiceCurrentDay = new DateTime();
                            noServiceCurrentDay = DateTime.ParseExact(noServiceStart, "yyyyMMdd", null);

                            DateTime noServiceFinishingPoint = new DateTime();
                            noServiceFinishingPoint = DateTime.ParseExact(noServiceEnd, "yyyyMMdd", null);

                            while (DateTime.Compare(noServiceCurrentDay, noServiceFinishingPoint) <= 0)
                            {
                                // New entry for the JArray
                                JValue newNonServiceEntry = new JValue(noServiceCurrentDay.ToString("yyyyMMdd"));

                                // If the day is not currently in the array then add it. 
                                if (noServiceDays.Contains(newNonServiceEntry) == false)
                                {
                                    noServiceDays.Add(newNonServiceEntry);
                                }

                                // Add 1 day
                                noServiceCurrentDay = noServiceCurrentDay.AddDays(1);
                            }

                        }
                    }
                    // Just one extra date
                    else
                    {
                        var noServiceStart = (specialDaysNonOperation[0]["StartDate"]).ToString().Replace("-", String.Empty);
                        var noServiceEnd = (specialDaysNonOperation[0]["EndDate"]).ToString().Replace("-", String.Empty);

                        DateTime noServiceCurrentDay = new DateTime();
                        noServiceCurrentDay = DateTime.ParseExact(noServiceStart, "yyyyMMdd", null);

                        DateTime noServiceFinishingPoint = new DateTime();
                        noServiceFinishingPoint = DateTime.ParseExact(noServiceEnd, "yyyyMMdd", null);

                        while (DateTime.Compare(noServiceCurrentDay, noServiceFinishingPoint) <= 0)
                        {
                            // New entry for the JArray
                            JValue newNonServiceEntry = new JValue(noServiceCurrentDay.ToString("yyyyMMdd"));

                            // If the day is not currently in the array then add it. 
                            if (noServiceDays.Contains(newNonServiceEntry) == false)
                            {
                                noServiceDays.Add(newNonServiceEntry);
                            }

                            // Add 1 day
                            noServiceCurrentDay = noServiceCurrentDay.AddDays(1);
                        }
                    }
                }

                // Days when there are extra services
                if (arrayOfTimings[i]["OperatingProfile"]["SpecialDaysOperation"]["DaysOfOperation"] != null)
                {
                    JArray specialDaysOperation = new JArray(arrayOfTimings[i]["OperatingProfile"]["SpecialDaysOperation"]["DaysOfOperation"]["DateRange"]);

                    // Multiple extra dates
                    if (specialDaysOperation[0].Type.ToString() == "Array")
                    {
                        for (int j = 0; j < specialDaysOperation[0].Count(); j++)
                        {

                            var extraServiceStart = (specialDaysOperation[0][j]["StartDate"]).ToString().Replace("-", String.Empty);
                            var extraServiceEnd = (specialDaysOperation[0][j]["EndDate"]).ToString().Replace("-", String.Empty);

                            DateTime extraServiceCurrentDay = new DateTime();
                            extraServiceCurrentDay = DateTime.ParseExact(extraServiceStart, "yyyyMMdd", null);

                            DateTime extraServiceFinishingPoint = new DateTime();
                            extraServiceFinishingPoint = DateTime.ParseExact(extraServiceEnd, "yyyyMMdd", null);

                            while (DateTime.Compare(extraServiceCurrentDay, extraServiceFinishingPoint) <= 0)
                            {
                                // New entry for the JArray
                                JValue newExtraServiceEntry = new JValue(extraServiceCurrentDay.ToString("yyyyMMdd"));

                                // If the day is not currently in the array then add it. 
                                if (extraServiceDays.Contains(newExtraServiceEntry) == false)
                                {
                                    extraServiceDays.Add(newExtraServiceEntry);
                                }

                                // Add 1 day
                                extraServiceCurrentDay = extraServiceCurrentDay.AddDays(1);
                            }

                        }
                    }
                    // Just one extra date
                    else
                    {
                        var extraServiceStart = (specialDaysOperation[0]["StartDate"]).ToString().Replace("-", String.Empty);
                        var extraServiceEnd = (specialDaysOperation[0]["EndDate"]).ToString().Replace("-", String.Empty);

                        DateTime extraServiceCurrentDay = new DateTime();
                        extraServiceCurrentDay = DateTime.ParseExact(extraServiceStart, "yyyyMMdd", null);

                        DateTime extraServiceFinishingPoint = new DateTime();
                        extraServiceFinishingPoint = DateTime.ParseExact(extraServiceEnd, "yyyyMMdd", null);

                        while (DateTime.Compare(extraServiceCurrentDay, extraServiceFinishingPoint) <= 0)
                        {
                            // New entry for the JArray
                            JValue newExtraServiceEntry = new JValue(extraServiceCurrentDay.ToString("yyyyMMdd"));

                            // If the day is not currently in the array then add it. 
                            if (extraServiceDays.Contains(newExtraServiceEntry) == false)
                            {
                                extraServiceDays.Add(newExtraServiceEntry);
                            }

                            // Add 1 day
                            extraServiceCurrentDay = extraServiceCurrentDay.AddDays(1);
                        }
                    }
                }
            }
        }


        private async void createCalendarTripsDatesAndTimesFromPatternAndTimes(JArray patternAndTimesArray, string serviceCode)
        {
            // Sort the array by departure time:
            patternAndTimesArray = new JArray(patternAndTimesArray.OrderBy(obj => obj["Departure"]));

            for (int i = 0; i < patternAndTimesArray.Count; i++)
            {
                calendarArray.Add((patternAndTimesArray[i]["Calendar"] + serviceCode + "-" + (i + 1) + "," + string.Join(",", patternAndTimesArray[i]["Days"]) + "," + patternAndTimesArray[i]["StartingDate"] + "," + patternAndTimesArray[i]["EndDate"]).ToString());
                batchCalendarArray.Add((patternAndTimesArray[i]["Calendar"] + serviceCode + "-" + (i + 1) + "," + string.Join(",", patternAndTimesArray[i]["Days"]) + "," + patternAndTimesArray[i]["StartingDate"] + "," + patternAndTimesArray[i]["EndDate"]).ToString());
                tripsArray.Add((serviceCode + "," + patternAndTimesArray[i]["Calendar"] + serviceCode + "-" + (i + 1) + "," + serviceCode + "-" + (i + 1) + ",," + patternAndTimesArray[i]["Direction"] + ",,").ToString());
                batchTripsArray.Add((serviceCode + "," + patternAndTimesArray[i]["Calendar"] + serviceCode + "-" + (i + 1) + "," + serviceCode + "-" + (i + 1) + ",," + patternAndTimesArray[i]["Direction"] + ",,").ToString());

                // Add extra services to calendar_datesarray
                if (patternAndTimesArray[i]["NoServiceDates"].Count() > 0)
                {
                    // Code 2 at the end means a date has been removed
                    for (int j = 0; j < patternAndTimesArray[i]["NoServiceDates"].Count(); j++)
                    {
                        calendar_datesArray.Add((patternAndTimesArray[i]["Calendar"] + serviceCode + "-" + (i + 1) + "," + patternAndTimesArray[i]["NoServiceDates"][j] + ",2").ToString());
                        batchCalendar_datesArray.Add((patternAndTimesArray[i]["Calendar"] + serviceCode + "-" + (i + 1) + "," + patternAndTimesArray[i]["NoServiceDates"][j] + ",2").ToString());
                    }
                }

                if (patternAndTimesArray[i]["ExtraServiceDates"].Count() > 0)
                {
                    // Code 1 at the end means a date has been added
                    for (int j = 0; j < patternAndTimesArray[i]["ExtraServiceDates"].Count(); j++)
                    {
                        calendar_datesArray.Add((patternAndTimesArray[i]["Calendar"] + serviceCode + "-" + (i + 1) + "," + patternAndTimesArray[i]["ExtraServiceDates"][j] + ",1").ToString());
                        batchCalendar_datesArray.Add((patternAndTimesArray[i]["Calendar"] + serviceCode + "-" + (i + 1) + "," + patternAndTimesArray[i]["ExtraServiceDates"][j] + ",1").ToString());
                    }
                }

                for (int j = 0; j < patternAndTimesArray[i]["Stops"].Count(); j++)
                {
                    stop_timesArray.Add((serviceCode + "-" + (i + 1) + "," + patternAndTimesArray[i]["Times"][j] + "," + patternAndTimesArray[i]["Times"][j] + "," + patternAndTimesArray[i]["Stops"][j] + "," + (j + 1) + ",,,,").ToString());
                    batchStop_timesArray.Add((serviceCode + "-" + (i + 1) + "," + patternAndTimesArray[i]["Times"][j] + "," + patternAndTimesArray[i]["Times"][j] + "," + patternAndTimesArray[i]["Stops"][j] + "," + (j + 1) + ",,,,").ToString());
                }
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    () =>
                    {
                        Calendar.Text = listAsString(calendarArray);
                        Trips.Text = listAsString(tripsArray);
                        StopTimes.Text = listAsString(stop_timesArray);
                        CalendarDates.Text = listAsString(calendar_datesArray);
                    });

        }

        private void clearUI()
        {
            XMLHolder.Text = "";
            JSONHolder.Text = "";
            ServiceName.Text = "";
            ServiceDescription.Text = "";
            Calendar.Text = "";
            Trips.Text = "";
            Routes.Text = "";
            Agency.Text = "";
            StopTimes.Text = "";
            CalendarDates.Text = "";
            Stops.Text = "";
        
        }

        // Helper function to print lists of strings as newline-separated strings
        private string listAsString(List<string> thisList)
        {
            string stringList = "";
            int lineCount = 0;
            foreach (var element in thisList)
            {
                lineCount++;
                if (lineCount > 100)
                {
                    stringList += "... list shortened to 100 lines ..." + "\n";
                    break;
                }
                stringList += element.ToString() + "\n";
            }
            return stringList;
        }
    }
}
