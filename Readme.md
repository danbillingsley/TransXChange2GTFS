# TransXChange2GTFS

A .NET Core 2 script to convert UK public transport timetables from TransXChange format to GTFS format. You can find such files for all public transport modes in Great Britain (GB) except trains from [Traveline Open Data](http://www.travelinedata.org.uk/). Trains timetables are not publicly available in TransXChange format.

This tool works for our purposes but it is not perfect. If you want GTFS files of GB public transport with quality and service level guarantees we recommend contacting [ITOWorld](http://www.itoworld.com/). Or finding us some money and we'll build that for you.

## Known limitations
1. Routes with multiple operators may only show the first operator. We expect this to be fixed at some point.
2. Dates of non-operation (Christmas Day etc...) are written to calendar_dates.txt. This file is much larger than necessary (explained below).
3. Not every GB bank holiday is included as a Date of non-operation. We intend to fix this.
4. We don't deal with modes of transport properly. Every journey (tram, tube, ferry, coach, whatever) will be called a Bus.
5. Other stuff may be wrong. Don't run your critical service on this tool.

## Trips and calendars
GTFS contains the concept of a trip (one journey of a bus, at a certain time, from the start of a route to the end of a route). GTFS also contains the concept of a calendar. A calendar describes which days those trips run on. Each trip runs to a calendar. **Currently we assign each trip a unique calendar even though many trips run to the same calendar**. This means that our calendar.txt and calendar_dates.txt file are much bigger than they need to be. To fix this we would need to look through the GTFS file before it is output and test for identical calendars, then merge them. **we haven't done this yet. We should. We haven't**.

## Geography
GTFS is designed for sharing the timetables of a transit authority or a single operator. In the UK, with dense public transport networks and huge number of operators this doesn't make a lot of sense. The tool can output a single collection of GTFS files (stops.txt, routes.txt, trips.txt, calendar.txt, agency.txt, calendar_dates.txt, stop_times.txt) from a single TransXChange file. Or it can output a single collection of GTFS files from thousands of TransXChange files stuck together. **For Great Britain a single GTFS file of every public transport journey would be enormous (100s of GB I'd guess), especially considering our trips and calendars issue**. For this reason we're probably going to be outputting GTFS files for the geographies used by Traveline.

## NaPTAN
National Public Transport Access Nodes (NaPTAN) is the national system for uniquely identifying points of access to public transport in England, Scotland and Wales. We have included a compressed extract of the complete NaPTAN stops dataset (from 20/04/2018) in this project. This must be unzipped before running the code. This dataset is updated daily and up-to-date data can be obtained [here](http://naptan.app.dft.gov.uk/datarequest/help). 

## Running the code
Set up the `data` directory as listed in the [README](./data/README.md).
You'll need some TransXChange files, get them from Traveline Open Data. TransXChange format files can change with time, this parser works with most files we tested it with in February 2018. I can't guarantee it'll work into the future.

.NET Core 2 code can run on just about any computer. Linux, Mac, Windows on Arm and Intel and AMD processors. Microsoft will have a good guide to doing that. We include the Visual Studio Solution to help you. We did this work on Windows, but it may work with the Mac version.

Upon completion a text file "report.txt" is generated. This contains a summary of results and a list of services that failed processing.

## Previous Versions
A previous version of this parser was written for UWP (Universal Windows). This has now been superceeded. It was really bad. Awful. Terrible. So bad. You really don't want to download that thing. But of course you can look at the history of this GitHub repo and find it if you really want. Seriously, don't.

## Example
An example of GTFS output from the tool is contained within yorkshireGTFS.zip. This contains schedules for all bus routes in Yorkshire on 06/03/2018.

## Docker Image

### Running the docker image

```bash
docker run -it --rm -v $(pwd)/data:/app/data open-innovations/transxchange2gtfs 
```

### Building the docker image

```bash
docker build -t open-innovations/transxchange2gtfs .
```

## License
MIT license. Use it for whatever you like. Attribution to ODILeeds, Thomas Forth, and Daniel Billingsley. Copyright Thomas Forth and Daniel Billingsley.
Example TransXChange files are from the Traveline National Dataset and are used under the [Open Government License v3](http://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/).
The Stops.txt is from NaPTAN and is used under the [Open Government License v3](http://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/).
