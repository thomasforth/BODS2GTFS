using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using GeoCoordinatePortable;
using ProtoBuf;
using SQLite;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Serialization;
using TransitRealtime;


// We can convert the gtfs-realtime.proto file to a C# class using the tool at https://protogen.marcgravell.com/
// This then lets us load and deserialize the GTFS-RT feedmessage as below.
// This can, presumably, be analyzed in the same way as I have been analyzing Siri-VM files.
FeedMessage GTFSRTFeedMessage;
using (var file = File.OpenRead("../../../../gtfsrt.bin"))
{
    GTFSRTFeedMessage = Serializer.Deserialize<FeedMessage>(file);
}


// We load in bus timetables so that we can match buses that ran to the information stored in here.
// Location updates for buses don't contain full information about the routes. We'll need this to recreate a GTFS timetable at the end.

Console.WriteLine("Load GB bus timetable.");
// bus timetables from https://data.bus-data.dft.gov.uk/timetable/download/
List<GTFSFile> GTFSFiles = new List<GTFSFile>();
GTFSFiles.Add(
    new GTFSFile()
    {
        path = @"../../../../itm_yorkshire_28Nov22_gtfs.zip",
        name = "Yorkshire Bus",
        place = "Yorkshire"
    });


CsvConfiguration csvconfig = new CsvConfiguration(CultureInfo.InvariantCulture) { MissingFieldFound = null, HeaderValidated = null };
foreach (GTFSFile GTFSFile in GTFSFiles)
//Parallel.ForEach(GTFSFiles, (GTFSFile) =>
{
    Console.WriteLine($"Parsing {GTFSFile.name}.");
    using (ZipArchive archive = new ZipArchive(File.OpenRead(GTFSFile.path)))
    {
        foreach (ZipArchiveEntry ZAE in archive.Entries)
        {
            using (StreamReader reader = new StreamReader(ZAE.Open()))
            {
                if (ZAE.FullName == "agency.txt")
                {
                    using (var csv = new CsvReader(reader, csvconfig))
                    {
                        GTFSFile.agencies = csv.GetRecords<Agency>().ToList();
                        GTFSFile.agency_noc2agency_idlookup = GTFSFile.agencies.ToLookup(x => x.agency_noc, x => x.agency_id);
                        GTFSFile.agency_id2agency = GTFSFile.agencies.GroupBy(x => x.agency_id).ToDictionary(x => x.Key, x => x.First());
                    }
                }
                if (ZAE.FullName == "stops.txt")
                {
                    using (var csv = new CsvReader(reader, csvconfig))
                    {
                        GTFSFile.stops = csv.GetRecords<StopCode>().ToList();
                        foreach(StopCode SC in GTFSFile.stops)
                        {
                            SC.stop_loc = new GeoCoordinate(SC.stop_lat, SC.stop_lon);
                        }
                        GTFSFile.stopslocdictionary = GTFSFile.stops.ToDictionary(x => x.stop_id, x => x.stop_loc);
                        GTFSFile.stopsdictionary = GTFSFile.stops.ToDictionary(x => x.stop_id, x => x);
                    }
                }
                if (ZAE.FullName == "stop_times.txt")
                {
                    using (var csv = new CsvReader(reader, csvconfig))
                    {
                        GTFSFile.stop_times = csv.GetRecords<StopTime>().ToList();
                        GTFSFile.tripidtostopcodelookup = GTFSFile.stop_times.ToLookup(x => x.trip_id, x => x.stop_id);
                    }
                }
                if (ZAE.FullName == "trips.txt")
                {
                    using (var csv = new CsvReader(reader, csvconfig))
                    {
                        GTFSFile.trips = csv.GetRecords<Trip>().ToList();
                        GTFSFile.triptoroutedictionary = GTFSFile.trips.ToDictionary(x => x.trip_id, x => x.route_id);
                        GTFSFile.routetotriplookup = GTFSFile.trips.ToLookup(x => x.route_id, x => x.trip_id);
                        GTFSFile.tripdictionary = GTFSFile.trips.ToDictionary(x => x.trip_id, x => x);
                    }
                }
                if (ZAE.FullName == "calendar.txt")
                {
                    using (var csv = new CsvReader(reader, csvconfig))
                    {
                        GTFSFile.calendar = csv.GetRecords<CalendarEntry>().ToList();
                    }
                }
                if (ZAE.FullName == "calendar_dates.txt")
                {
                    using (var csv = new CsvReader(reader, csvconfig))
                    {
                        GTFSFile.calendar_dates = csv.GetRecords<calendar_date>().ToList();
                    }
                }
                if (ZAE.FullName == "routes.txt")
                {
                    using (var csv = new CsvReader(reader, csvconfig))
                    {
                        GTFSFile.routes = csv.GetRecords<Route>().ToList();
                        GTFSFile.routemodedictionary = GTFSFile.routes.ToDictionary(x => x.route_id, x => x.route_type);
                        GTFSFile.routeagencylookup = GTFSFile.routes.ToLookup(x => x.agency_id, x => x);
                        GTFSFile.routedictionary = GTFSFile.routes.ToDictionary(x => x.route_id, x => x);
                    }
                }
            }
        }
    }
}


// We load Siri files containing bus location updates.
// In future we should work GTFS-RT files, but for the moment I'm struggling to parse these in .NET.
// The GTFS-RT bindings for .NET have been poorly maintained in the past and still don't work out of the box with .NET core.
// But there seems to be some efforts to get them working. https://github.com/MobilityData/gtfs-realtime-bindings/
// I should try and manually parse the proto-buf GTFS-RT format.

Console.WriteLine("Parsing Siri files.");
List<string> SiriFiles = Directory.GetFiles(@"../../../../BODS_2022_12_03").OrderBy(x => x).ToList();

//List<string> SiriFiles = Directory.GetFiles(@"C:\Users\ThomasForth\PersonalOneDrive\OneDrive\imactivate projects\Transport\BusTracking2\BODS_archive").OrderBy(x => x).ToList();
//SiriFiles = SiriFiles.Where(x => x.Contains("BODS_2021_07_02")).ToList();
//SiriFiles.RemoveAll(x => x.Contains("BODS_2021_07_03") || x.Contains("BODS_2021_07_04"));

XmlSerializer serializer = new XmlSerializer(typeof(Siri));

// for now we do all the work in RAM. This could be moved to SQLite or DuckDB or similar if RAM is an issue, but we probably never want to analyse more than a few days at once.
ConcurrentBag<BusDetail> BusDetailsBag = new ConcurrentBag<BusDetail>();
long count = 0;

//foreach(string filepath in SiriFiles)
Parallel.ForEach(SiriFiles, (filepath) =>
{
    count++;
    using (ZipArchive archive = new ZipArchive(File.OpenRead(filepath)))
    {
        Siri EveryBusInGB = (Siri)serializer.Deserialize(archive.Entries.First().Open());
        foreach (SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivity Vehicle in EveryBusInGB.ServiceDelivery.VehicleMonitoringDelivery.VehicleActivity)
        {
            if (GTFSFiles[0].agency_noc2agency_idlookup.Contains(Vehicle.MonitoredVehicleJourney.OperatorRef))
            {
                BusDetail BD = new BusDetail();

                BD.VehicleRef = Vehicle.MonitoredVehicleJourney.VehicleRef;
                BD.Latitude = Math.Round(Vehicle.MonitoredVehicleJourney.VehicleLocation.Latitude, 8);
                BD.Longitude = Math.Round(Vehicle.MonitoredVehicleJourney.VehicleLocation.Longitude, 8);
                //BD.Bearing = Vehicle.MonitoredVehicleJourney.Bearing;
                BD.Destination = Vehicle.MonitoredVehicleJourney.DestinationRef;
                BD.Operator = Vehicle.MonitoredVehicleJourney.OperatorRef;
                BD.Service = Vehicle.MonitoredVehicleJourney.LineRef;
                //BD.DateTime = EveryBusInGB.ServiceDelivery.ResponseTimestamp;
                BD.TimeString = EveryBusInGB.ServiceDelivery.ResponseTimestamp.TimeOfDay.ToString("hh\\hmm");
                //BD.DateString = EveryBusInGB.ServiceDelivery.ResponseTimestamp.Date.ToString("yyyy-MM-dd");
                //BD.DayOfWeek = EveryBusInGB.ServiceDelivery.ResponseTimestamp.DayOfWeek.ToString("G");

                string agency_id = GTFSFiles[0].agency_noc2agency_idlookup[BD.Operator].First(); // I shouldn't do the FIRST here.
                if (agency_id != null)
                {
                    IEnumerable<string> RouteIDsForThisBus = GTFSFiles[0].routeagencylookup[agency_id].Where(x => x.route_short_name == BD.Service).Select(x => x.route_id);
                    if (RouteIDsForThisBus.FirstOrDefault() != null)
                    {
                        IEnumerable<string> TripIDsForTheseRoute = GTFSFiles[0].routetotriplookup[RouteIDsForThisBus.First()]; // I shouldn't do the FIRST here.
                        if (TripIDsForTheseRoute.FirstOrDefault() != null)
                        {
                            IEnumerable<string> StopCodesOnThisRouteID = GTFSFiles[0].tripidtostopcodelookup[TripIDsForTheseRoute.First()]; // I shouldn't do the FIRST here.
                            if (StopCodesOnThisRouteID.FirstOrDefault() != null)
                            {
                                BD.NearestStopOnRoute = StopCodesOnThisRouteID.OrderBy(x => new GeoCoordinate(BD.Latitude, BD.Longitude).GetDistanceTo(GTFSFiles[0].stopslocdictionary[x])).First();
                                BD.NearestStopDistance = new GeoCoordinate(BD.Latitude, BD.Longitude).GetDistanceTo(GTFSFiles[0].stopslocdictionary[BD.NearestStopOnRoute]);
                                BD.EstimatedTripID = TripIDsForTheseRoute.First();
                                BD.EstimatedRouteID = RouteIDsForThisBus.First();
                            }
                        }
                    }
                }
                /*
                if (Vehicle.Extensions != null)
                {
                    BD.Capacity = Vehicle.Extensions.VehicleJourney.SeatedCapacity;
                    BD.Occupancy = Vehicle.Extensions.VehicleJourney.SeatedOccupancy;
                }
                */

                if (BD.NearestStopOnRoute != null && BD.Service != null && BD.NearestStopDistance < 200)
                {
                    BusDetailsBag.Add(BD);
                }
            }
        }
    }
    Console.WriteLine($"{count} of {SiriFiles.Count} files parsed.");

    // Now delete duplicate stops (this also gets rid of long periods where the vehicle is waiting)
    // select * from BusDetail order by Operator, Service, VehicleRef, TimeString

});


List<BusDetail> OrderedBusLocations = BusDetailsBag.OrderBy(x => x.Operator).ThenBy(x => string.Concat(x.Service, x.Destination)).ThenBy(x => x.VehicleRef).ThenBy(x => x.TimeString).ToList();

List<BusDetail> FilteredOrderedBusLocations = new List<BusDetail>();
for (int i = 1; i < OrderedBusLocations.Count; i++)
{
    if (OrderedBusLocations[i - 1].NearestStopOnRoute != OrderedBusLocations[i].NearestStopOnRoute)
    {
        FilteredOrderedBusLocations.Add(OrderedBusLocations[i]);
    }
}


// Output a database of the location of the buses parsed from the location reports for debugging
using (SQLiteConnection BusDB = new SQLiteConnection("BusDB.db"))
{
    BusDB.CreateTable<BusDetail>();
    BusDB.InsertAll(FilteredOrderedBusLocations);
}


// Now write this timetable as GTFS
// write GTFS txts.
// agency.txt, calendar.txt, calendar_dates.txt, routes.txt, stop_times.txt, stops.txt, trips.txt
if (Directory.Exists("output") == false)
{
    Directory.CreateDirectory("output");
}

Console.WriteLine("Creating and writing agency.txt");

List<Agency> AgencyList = FilteredOrderedBusLocations.Select(x => x.Operator).Distinct().Select(x => GTFSFiles.First().agency_id2agency[GTFSFiles.First().agency_noc2agency_idlookup[x].First()]).ToList();
using (TextWriter agencyTextWriter = File.CreateText(@"output/agency.txt"))
using (CsvWriter agencyCSVwriter = new CsvWriter(agencyTextWriter, CultureInfo.InvariantCulture))
    agencyCSVwriter.WriteRecords(AgencyList);


Console.WriteLine("Creating and writing stops.txt");
List<StopCode> GTFSStopsList = FilteredOrderedBusLocations.Select(x => x.NearestStopOnRoute).Distinct().Select(x => GTFSFiles.First().stopsdictionary[x]).ToList();
using (TextWriter stopsTextWriter = File.CreateText(@"output/stops.txt"))
using (CsvWriter stopsCSVwriter = new CsvWriter(stopsTextWriter, CultureInfo.InvariantCulture))
    stopsCSVwriter.WriteRecords(GTFSStopsList);

Console.WriteLine("Creating and writing routes.txt");
List<Route> RoutesList = FilteredOrderedBusLocations.Select(x => x.EstimatedRouteID).Distinct().Select(x => GTFSFiles.First().routedictionary[x]).ToList();
using (TextWriter routesTextWriter = File.CreateText(@"output/routes.txt"))
using (CsvWriter routesCSVwriter = new CsvWriter(routesTextWriter, CultureInfo.InvariantCulture))
    routesCSVwriter.WriteRecords(RoutesList);

Console.WriteLine("Creating and writing trips.txt");
List<Trip> TripList = FilteredOrderedBusLocations.Select(x => new Tuple<string, string>(x.EstimatedTripID, string.Concat(x.EstimatedTripID, x.VehicleRef))).Distinct().Select(x => new Trip() { trip_mode = "3", trip_id = x.Item2, route_id = GTFSFiles.First().tripdictionary[x.Item1].route_id, service_id = GTFSFiles.First().tripdictionary[x.Item1].service_id }).ToList();
using (TextWriter tripsTextWriter = File.CreateText(@"output/trips.txt"))
using (CsvWriter tripsCSVwriter = new CsvWriter(tripsTextWriter, CultureInfo.InvariantCulture))
    tripsCSVwriter.WriteRecords(TripList);


Console.WriteLine("Creating and writing calendar.txt");
List<CalendarEntry> CalendarList = TripList.Select(x => x.service_id).Distinct().Select(x => new CalendarEntry() { service_id = x, start_date = 20221203, end_date = 20221203, friday = 1, monday = 1, saturday = 1, sunday = 1, thursday = 1, tuesday = 1, wednesday = 1 }).ToList();
using (TextWriter calendarTextWriter = File.CreateText(@"output/calendar.txt"))
using (CsvWriter calendarCSVwriter = new CsvWriter(calendarTextWriter, CultureInfo.InvariantCulture))
    calendarCSVwriter.WriteRecords(CalendarList);


// we're using the allowed (and used in France) calendar_dates.txt method for defining when services run.
Console.WriteLine("Creating and writing calendar_dates.txt");
List<calendar_date> CalendarDates = TripList.Select(x => x.service_id).Distinct().Select(x => new calendar_date() { service_id= x, exception_type = 1, date = "20221203" }).ToList();
using (TextWriter tripsTextWriter = File.CreateText(@"output/calendar_dates.txt"))
using (CsvWriter tripsCSVwriter = new CsvWriter(tripsTextWriter, CultureInfo.InvariantCulture))
    tripsCSVwriter.WriteRecords(CalendarDates);


Console.WriteLine("Creating and writing stop_times.txt");
// minimally required fields are trip_id, arrival_time and/or departure_time, stop_id, and stop_sequence (must be sequential)
List<StopTime> StopTimesList = new List<StopTime>();

Dictionary<string, List<BusDetail>> TripIDGroup = FilteredOrderedBusLocations.GroupBy(x => string.Concat(x.EstimatedTripID, x.VehicleRef)).ToDictionary(x => x.Key, x => x.ToList());
foreach(string TripID in TripIDGroup.Keys)
{
    List<BusDetail> StopsForThisTripID = TripIDGroup[TripID];
    for(int i = 0; i < StopsForThisTripID.Count; i++)
    {
        BusDetail BD = StopsForThisTripID[i];
        StopTimesList.Add(new StopTime() { stop_sequence = i, arrival_time = BD.TimeString.Replace('h',':') + ":00", departure_time = BD.TimeString.Replace('h', ':') + ":00", stop_id = BD.NearestStopOnRoute, trip_id = TripID });
    }
}

using (TextWriter stopTimeTextWriter = File.CreateText(@"output/stop_times.txt"))
{
    using (CsvWriter stopTimeCSVwriter = new CsvWriter(stopTimeTextWriter, CultureInfo.InvariantCulture))
    {
        stopTimeCSVwriter.WriteRecords(StopTimesList);
    }
}


Console.WriteLine("Creating a GTFS .zip file.");
string filename = "realtimetable_GB_20221203.zip";
if (File.Exists(filename))
{
    File.Delete(filename);
}
ZipFile.CreateFromDirectory("output", filename, CompressionLevel.Optimal, false, Encoding.UTF8);

Console.WriteLine("You may wish to validate the GTFS output using a tool such as https://github.com/google/transitfeed/");


public class BusDetail
{
    public string Service { get; set; }
    public string Destination { get; set; }
    public string NearestStopOnRoute { get; set; }
    public double NearestStopDistance { get; set; }
    public bool IntoTown { get; set; }
    public bool OutOfTown { get; set; }
    public string Operator { get; set; }
    public string VehicleRef { get; set; }
    public string TimeString { get; set; }
    public string DateString { get; set; }
    public string DayOfWeek { get; set; }
    public string EstimatedTripID { get; set; }
    public string EstimatedRouteID { get; set; }
    public DateTime DateTime { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public decimal Bearing { get; set; }
    public int? Occupancy { get; set; }
    public int? Capacity { get; set; }
    public decimal? Speed { get; set; }
}


public class GTFSFile
{
    public string path { get; set; }
    public string name { get; set; }
    public string place { get; set; }
    public DateTime DateToAnalyse { get; set; }
    public GeoCoordinate LocationToAnalyse { get; set; }
    public List<Agency> agencies { get; set; }
    public ILookup<string, string> agency_noc2agency_idlookup { get; set;}
    public Dictionary<string, Agency> agency_id2agency { get; set;}
    public List<StopTime> stop_times { get; set; }
    public ILookup<string, string> tripidtostopcodelookup { get; set; }
    public List<StopCode> stops { get; set; }
    public Dictionary<string, GeoCoordinate> stopslocdictionary { get; set; }
    public Dictionary<string, StopCode> stopsdictionary { get; set; }
    public List<Route> routes { get; set; }
    public Dictionary<string, int> routemodedictionary { get; set; }
    public ILookup<string, Route> routeagencylookup { get; set; }
    public Dictionary<string, Route> routedictionary { get; set; }
    public HashSet<string> stopcodeswithinreach { get; set; }
    public List<CalendarEntry> calendar { get; set; }
    public HashSet<string> serviceidsrunningtoday { get; set; }
    public List<Trip> trips { get; set; }
    public Dictionary<string, string> triptoroutedictionary { get; set; }
    public Dictionary<string, Trip> tripdictionary { get; set; }
    public ILookup<string, string> routetotriplookup { get; set; }
    public HashSet<string> tripidsrunningtoday { get; set; }
    public List<calendar_date> calendar_dates { get; set; }
}


public class calendar_date
{
    public string service_id { get; set; }
    public string date { get; set; }
    public int exception_type { get; set; }
}
public class CalendarEntry
{
    public string service_id { get; set; }
    public int monday { get; set; }
    public int tuesday { get; set; }
    public int wednesday { get; set; }
    public int thursday { get; set; }
    public int friday { get; set; }
    public int saturday { get; set; }
    public int sunday { get; set; }
    public int start_date { get; set; }
    public int end_date { get; set; }
}

public class Route
{
    public string route_id { get; set; }
    public string agency_id { get; set; }
    public string route_short_name { get; set; }
    public string route_long_name { get; set; }
    public string route_desc { get; set; }
    public int route_type { get; set; }
    public string route_url { get; set; }
    public string route_color { get; set; }
    public string route_text_color { get; set; }
}

public class Trip
{
    public string route_id { get; set; }
    public string service_id { get; set; }
    // public string trip_short_name { get; set; }
    //public string trip_headsign { get; set; }
    //public string route_short_name { get; set; }
    //public int direction_id { get; set; }
    //public string block_id { get; set; }
    //public string shape_id { get; set; }
    //public int wheelchair_accessible { get; set; }
    //public int trip_bikes_allowed { get; set; }
    public string trip_id { get; set; }
    public string trip_mode { get; set; }
}
public class ActiveVehicleAtTime
{
    public TimeSpan Time { get; set; }
    public string Name { get; set; }
    public string Place { get; set; }
    public int Mode { get; set; }
    public int Count { get; set; }
    public string TripIDs { get; set; }
}

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

public class Agency
{
    public string agency_id { get; set; }
    public string agency_name { get; set; }
    public string agency_url { get; set; }
    public string agency_timezone { get; set; }
    public string agency_lang { get; set; }
    public string agency_phone { get; set; }
    public string agency_noc { get; set; }
}
public class StopCode
{
    public string stop_id { get; set; }
    public string stop_code { get; set; }
    public string stop_name { get; set; }
    public double stop_lat { get; set; }
    public double stop_lon { get; set; }
    public string stop_url { get; set; }
    public GeoCoordinate stop_loc { get; set; }
}


// NOTE: Generated code may require at least .NET Framework 4.5 or .NET Core/Standard 2.0.
/// <remarks/>
[System.SerializableAttribute()]
[System.ComponentModel.DesignerCategoryAttribute("code")]
[System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.siri.org.uk/siri")]
[System.Xml.Serialization.XmlRootAttribute(Namespace = "http://www.siri.org.uk/siri", IsNullable = false)]
public partial class Siri
{
    /// <remarks/>
    public SiriServiceDelivery ServiceDelivery { get; set; }
    [System.Xml.Serialization.XmlAttributeAttribute()]
    public decimal version { get; set; }
}


[System.SerializableAttribute()]
[System.ComponentModel.DesignerCategoryAttribute("code")]
[System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.siri.org.uk/siri")]
public partial class SiriServiceDelivery
{
    public DateTime ResponseTimestamp { get; set; }
    public string ProducerRef { get; set; }
    public SiriServiceDeliveryVehicleMonitoringDelivery VehicleMonitoringDelivery { get; set; }
}

[System.SerializableAttribute()]
[System.ComponentModel.DesignerCategoryAttribute("code")]
[System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.siri.org.uk/siri")]
public partial class SiriServiceDeliveryVehicleMonitoringDelivery
{
    public System.DateTime ResponseTimestamp { get; set; }
    public string RequestMessageRef { get; set; }

    public System.DateTime ValidUntil { get; set; }

    [System.Xml.Serialization.XmlElementAttribute(DataType = "duration")]
    public string ShortestPossibleCycle { get; set; }

    [System.Xml.Serialization.XmlElementAttribute("VehicleActivity")]
    public SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivity[] VehicleActivity { get; set; }
}

/// <remarks/>
[System.SerializableAttribute()]
[System.ComponentModel.DesignerCategoryAttribute("code")]
[System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.siri.org.uk/siri")]
public partial class SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivity
{

    private System.DateTime recordedAtTimeField;

    private string itemIdentifierField;

    private System.DateTime validUntilTimeField;

    private SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityMonitoredVehicleJourney monitoredVehicleJourneyField;

    private SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensions extensionsField;

    /// <remarks/>
    public System.DateTime RecordedAtTime { get; set; }
    public string ItemIdentifier { get; set; }
    public System.DateTime ValidUntilTime { get; set; }
    public SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityMonitoredVehicleJourney MonitoredVehicleJourney { get; set; }
    public SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensions Extensions { get; set; }
}

[System.SerializableAttribute()]
[System.ComponentModel.DesignerCategoryAttribute("code")]
[System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.siri.org.uk/siri")]
public partial class SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityMonitoredVehicleJourney
{

    private string lineRefField;

    private string directionRefField;

    private string publishedLineNameField;

    private string operatorRefField;

    private string originRefField;

    private string originNameField;

    private string destinationRefField;

    private string destinationNameField;

    private System.DateTime originAimedDepartureTimeField;

    private bool originAimedDepartureTimeFieldSpecified;

    private System.DateTime destinationAimedArrivalTimeField;

    private bool destinationAimedArrivalTimeFieldSpecified;

    private SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityMonitoredVehicleJourneyVehicleLocation vehicleLocationField;

    private decimal bearingField;

    private bool bearingFieldSpecified;

    private string occupancyField;

    private string blockRefField;

    private string vehicleJourneyRefField;

    private string vehicleRefField;

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
    public string DirectionRef
    {
        get
        {
            return this.directionRefField;
        }
        set
        {
            this.directionRefField = value;
        }
    }

    /// <remarks/>
    public string PublishedLineName
    {
        get
        {
            return this.publishedLineNameField;
        }
        set
        {
            this.publishedLineNameField = value;
        }
    }

    /// <remarks/>
    public string OperatorRef
    {
        get
        {
            return this.operatorRefField;
        }
        set
        {
            this.operatorRefField = value;
        }
    }

    /// <remarks/>
    public string OriginRef
    {
        get
        {
            return this.originRefField;
        }
        set
        {
            this.originRefField = value;
        }
    }

    /// <remarks/>
    public string OriginName
    {
        get
        {
            return this.originNameField;
        }
        set
        {
            this.originNameField = value;
        }
    }

    /// <remarks/>
    public string DestinationRef
    {
        get
        {
            return this.destinationRefField;
        }
        set
        {
            this.destinationRefField = value;
        }
    }

    /// <remarks/>
    public string DestinationName
    {
        get
        {
            return this.destinationNameField;
        }
        set
        {
            this.destinationNameField = value;
        }
    }

    /// <remarks/>
    public System.DateTime OriginAimedDepartureTime
    {
        get
        {
            return this.originAimedDepartureTimeField;
        }
        set
        {
            this.originAimedDepartureTimeField = value;
        }
    }

    /// <remarks/>
    [System.Xml.Serialization.XmlIgnoreAttribute()]
    public bool OriginAimedDepartureTimeSpecified
    {
        get
        {
            return this.originAimedDepartureTimeFieldSpecified;
        }
        set
        {
            this.originAimedDepartureTimeFieldSpecified = value;
        }
    }

    /// <remarks/>
    public System.DateTime DestinationAimedArrivalTime
    {
        get
        {
            return this.destinationAimedArrivalTimeField;
        }
        set
        {
            this.destinationAimedArrivalTimeField = value;
        }
    }

    /// <remarks/>
    [System.Xml.Serialization.XmlIgnoreAttribute()]
    public bool DestinationAimedArrivalTimeSpecified
    {
        get
        {
            return this.destinationAimedArrivalTimeFieldSpecified;
        }
        set
        {
            this.destinationAimedArrivalTimeFieldSpecified = value;
        }
    }

    /// <remarks/>
    public SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityMonitoredVehicleJourneyVehicleLocation VehicleLocation
    {
        get
        {
            return this.vehicleLocationField;
        }
        set
        {
            this.vehicleLocationField = value;
        }
    }

    /// <remarks/>
    public decimal Bearing
    {
        get
        {
            return this.bearingField;
        }
        set
        {
            this.bearingField = value;
        }
    }

    /// <remarks/>
    [System.Xml.Serialization.XmlIgnoreAttribute()]
    public bool BearingSpecified
    {
        get
        {
            return this.bearingFieldSpecified;
        }
        set
        {
            this.bearingFieldSpecified = value;
        }
    }

    /// <remarks/>
    public string Occupancy
    {
        get
        {
            return this.occupancyField;
        }
        set
        {
            this.occupancyField = value;
        }
    }

    /// <remarks/>
    public string BlockRef
    {
        get
        {
            return this.blockRefField;
        }
        set
        {
            this.blockRefField = value;
        }
    }

    /// <remarks/>
    public string VehicleJourneyRef
    {
        get
        {
            return this.vehicleJourneyRefField;
        }
        set
        {
            this.vehicleJourneyRefField = value;
        }
    }

    /// <remarks/>
    public string VehicleRef
    {
        get
        {
            return this.vehicleRefField;
        }
        set
        {
            this.vehicleRefField = value;
        }
    }
}

/// <remarks/>
[System.SerializableAttribute()]
[System.ComponentModel.DesignerCategoryAttribute("code")]
[System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.siri.org.uk/siri")]
public partial class SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityMonitoredVehicleJourneyVehicleLocation
{
    public double Longitude { get; set; }
    public double Latitude { get; set; }
}

/// <remarks/>
[System.SerializableAttribute()]
[System.ComponentModel.DesignerCategoryAttribute("code")]
[System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.siri.org.uk/siri")]
public partial class SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensions
{

    private SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensionsVehicleJourney vehicleJourneyField;

    /// <remarks/>
    public SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensionsVehicleJourney VehicleJourney
    {
        get
        {
            return this.vehicleJourneyField;
        }
        set
        {
            this.vehicleJourneyField = value;
        }
    }
}

/// <remarks/>
[System.SerializableAttribute()]
[System.ComponentModel.DesignerCategoryAttribute("code")]
[System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.siri.org.uk/siri")]
public partial class SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensionsVehicleJourney
{

    private SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensionsVehicleJourneyOperational operationalField;

    private string vehicleUniqueIdField;

    private string driverRefField;

    private int seatedOccupancyField;

    private bool seatedOccupancyFieldSpecified;

    private int seatedCapacityField;

    private bool seatedCapacityFieldSpecified;

    private byte wheelchairOccupancyField;

    private bool wheelchairOccupancyFieldSpecified;

    private byte wheelchairCapacityField;

    private bool wheelchairCapacityFieldSpecified;

    private string occupancyThresholdsField;

    /// <remarks/>
    public SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensionsVehicleJourneyOperational Operational
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
    public string VehicleUniqueId
    {
        get
        {
            return this.vehicleUniqueIdField;
        }
        set
        {
            this.vehicleUniqueIdField = value;
        }
    }

    /// <remarks/>
    public string DriverRef
    {
        get
        {
            return this.driverRefField;
        }
        set
        {
            this.driverRefField = value;
        }
    }

    /// <remarks/>
    public int SeatedOccupancy
    {
        get
        {
            return this.seatedOccupancyField;
        }
        set
        {
            this.seatedOccupancyField = value;
        }
    }

    /// <remarks/>
    [System.Xml.Serialization.XmlIgnoreAttribute()]
    public bool SeatedOccupancySpecified
    {
        get
        {
            return this.seatedOccupancyFieldSpecified;
        }
        set
        {
            this.seatedOccupancyFieldSpecified = value;
        }
    }

    /// <remarks/>
    public int SeatedCapacity
    {
        get
        {
            return this.seatedCapacityField;
        }
        set
        {
            this.seatedCapacityField = value;
        }
    }

    /// <remarks/>
    [System.Xml.Serialization.XmlIgnoreAttribute()]
    public bool SeatedCapacitySpecified
    {
        get
        {
            return this.seatedCapacityFieldSpecified;
        }
        set
        {
            this.seatedCapacityFieldSpecified = value;
        }
    }

    /// <remarks/>
    public byte WheelchairOccupancy
    {
        get
        {
            return this.wheelchairOccupancyField;
        }
        set
        {
            this.wheelchairOccupancyField = value;
        }
    }

    /// <remarks/>
    [System.Xml.Serialization.XmlIgnoreAttribute()]
    public bool WheelchairOccupancySpecified
    {
        get
        {
            return this.wheelchairOccupancyFieldSpecified;
        }
        set
        {
            this.wheelchairOccupancyFieldSpecified = value;
        }
    }

    /// <remarks/>
    public byte WheelchairCapacity
    {
        get
        {
            return this.wheelchairCapacityField;
        }
        set
        {
            this.wheelchairCapacityField = value;
        }
    }

    /// <remarks/>
    [System.Xml.Serialization.XmlIgnoreAttribute()]
    public bool WheelchairCapacitySpecified
    {
        get
        {
            return this.wheelchairCapacityFieldSpecified;
        }
        set
        {
            this.wheelchairCapacityFieldSpecified = value;
        }
    }

    /// <remarks/>
    public string OccupancyThresholds
    {
        get
        {
            return this.occupancyThresholdsField;
        }
        set
        {
            this.occupancyThresholdsField = value;
        }
    }
}

/// <remarks/>
[System.SerializableAttribute()]
[System.ComponentModel.DesignerCategoryAttribute("code")]
[System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.siri.org.uk/siri")]
public partial class SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensionsVehicleJourneyOperational
{

    private SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensionsVehicleJourneyOperationalTicketMachine ticketMachineField;

    /// <remarks/>
    public SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensionsVehicleJourneyOperationalTicketMachine TicketMachine
    {
        get
        {
            return this.ticketMachineField;
        }
        set
        {
            this.ticketMachineField = value;
        }
    }
}

/// <remarks/>
[System.SerializableAttribute()]
[System.ComponentModel.DesignerCategoryAttribute("code")]
[System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true, Namespace = "http://www.siri.org.uk/siri")]
public partial class SiriServiceDeliveryVehicleMonitoringDeliveryVehicleActivityExtensionsVehicleJourneyOperationalTicketMachine
{

    private string ticketMachineServiceCodeField;

    private ushort journeyCodeField;

    /// <remarks/>
    public string TicketMachineServiceCode
    {
        get
        {
            return this.ticketMachineServiceCodeField;
        }
        set
        {
            this.ticketMachineServiceCodeField = value;
        }
    }

    /// <remarks/>
    public ushort JourneyCode
    {
        get
        {
            return this.journeyCodeField;
        }
        set
        {
            this.journeyCodeField = value;
        }
    }
}

