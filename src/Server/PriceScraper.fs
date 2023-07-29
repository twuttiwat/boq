module PriceScraper

open System
open System.Collections.Generic
open System.IO
open System.Linq
open System.Net
open System.Net.Http
open System.Text
open FSharp.Data
open Microsoft.Extensions.Logging

type System.String with
    member x.ReplaceFirst(search:string, replace:string) =
        let pos = x.IndexOf(search)
        if (pos < 0) then x
        else x.Substring(0, pos) + replace + x.Substring(pos + search.Length)

type System.DateTime with
    member x.BDYear = x.Year + 543
    member x.YearMonth = x.BDYear,x.Month

type YearMonth = int*int

type IScraper =
    abstract member Start: RefreshInterval: int -> YearMonths: YearMonth list -> unit

module BkkScraper =
    [<Literal>]
    let Province = 10
    [<Literal>]
    let ProvinceName = "Bangkok"

    let private requestPriceBytes (priceUrl:string) =
        task {
             let cookieContainer = CookieContainer()
             use handler = new HttpClientHandler()
             handler.CookieContainer <- cookieContainer
             use client = new HttpClient(handler)

             let selectMonthUrl = "http://www.indexpr.moc.go.th/PRICE_PRESENT/SelectCsi_month_REGION.asp?region=0"
             let! _ = client.GetAsync selectMonthUrl

             let formVals = [ "DDGroupCode","" ; "Submit","%B5%A1%C5%A7" ]
                            |> List.map (fun (x,y) -> KeyValuePair<string,string>(x,y))
             use content = new FormUrlEncodedContent(formVals)

             return! client
                         .PostAsync(priceUrl, content)
                         .Result.Content.ReadAsByteArrayAsync()
        }

    let private decodeThai bytes =
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
        let thaiEncoding = Encoding.GetEncoding("windows-874")
        let response = thaiEncoding.GetString(bytes, 0, bytes.Length - 1)
        response.ReplaceFirst("windows-874", "utf-8")


    let priceUrl year month =
        $"http://www.indexpr.moc.go.th/PRICE_PRESENT/tablecsi_month_region.asp?DDMonth=%02i{month}&DDYear={year}&DDProvince={Province}&B1=" + "%B5%A1%C5%A7"

    let outputPath year month =
        $"./data/raw/price/{year}-%02i{month}/{Province}.html"

    let downloadPriceFile (logger:ILogger) (priceUrl:string) outputPath =
        task {
            let! bytes = requestPriceBytes priceUrl
            let response = decodeThai bytes

            if response.Contains "error" |> not then
                let file = FileInfo(outputPath)
                file.Directory.Create()
                logger.LogInformation("Writing price file to {OutputPath}", outputPath)
                do! File.WriteAllTextAsync(outputPath, response)
                return true
            else
                logger.LogError("Error while requesting price on {PriceUrl}", priceUrl)
                logger.LogError("{Response}", response)
                return false
        }

    let alreadyDownloaded year month =
        outputPath year month
        |> File.Exists


    type RefreshInterval = int
    type YearMonth = int*int
    type State = { YearMonthsQueue: Set<YearMonth>; RefreshInterval: int }
    type Msg =
        | Start of (RefreshInterval*YearMonth list)
        | Refresh

    let initState () =
        let oneHour = 1000 * 60 * 60
        { YearMonthsQueue = Set.empty; RefreshInterval = oneHour }

    let scrapeMonth (logger: ILogger) (year,month) = async {
        if alreadyDownloaded year month then
            logger.LogInformation("File of {Year}-{Month}-{Province} already downloaded. Do nothing.", year, month, ProvinceName)
            return Some (year, month)
        else
            let priceUrl = priceUrl year month
            let outputPath = outputPath year month

            let! canDownload = downloadPriceFile logger priceUrl outputPath |> Async.AwaitTask
            return if canDownload then Some (year, month) else None
    }

    let scrape (logger: ILogger) (yearMonths: YearMonth list) =
        yearMonths
        |> List.map (scrapeMonth logger)
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.choose id

    type Scraper (logger: ILogger)  =
        let agent = MailboxProcessor<Msg>.Start(fun inbox ->
            logger.LogInformation("BkkScraper ready...")

            let rec messageLoop (state: State) = async {
                let! msg = inbox.Receive ()
                match msg with
                | Start (refreshInterval,yearMonths) ->
                    let newQueue = state.YearMonthsQueue + (yearMonths |> Set.ofList)
                    let newState = { state with YearMonthsQueue =  newQueue
                                                RefreshInterval = refreshInterval }

                    inbox.Post Refresh

                    return! messageLoop newState

                | Refresh ->
                    let fetchedYearMonths = scrape logger (state.YearMonthsQueue |> Set.toList)
                    let newQueue = state.YearMonthsQueue - (fetchedYearMonths |> Set.ofArray)
                    let newState = { state with YearMonthsQueue =  newQueue }

                    do! Async.Sleep state.RefreshInterval
                    inbox.Post Refresh

                    return! messageLoop state
            }

            initState ()
            |> messageLoop
        )

        interface IScraper with
            member _.Start refreshInterval yearMonths = agent.Post (Start (refreshInterval,yearMonths))

module ProvinceCsv =
    [<Literal>]
    let ResolutionFolder = __SOURCE_DIRECTORY__
    type ProvinceCsv = CsvProvider<"./data/province.csv", ResolutionFolder=ResolutionFolder>

    let rec findParent (directory: string) (fileToFind: string) =
        let path =
            if Directory.Exists(directory) then directory else Directory.GetParent(directory).FullName

        let files = Directory.GetFiles(path)
        if files.Any(fun file -> Path.GetFileName(file).ToLower() = fileToFind.ToLower())
        then path
        else findParent (DirectoryInfo(path).Parent.FullName) fileToFind

    let provinceCsvPath() = Path.Combine(Env.serverPath(), "data", "province.csv")

    let load () =
        let csv = provinceCsvPath ()
        ProvinceCsv.Load(csv).Rows |> List.ofSeq

module ProvinceScraper =
    let private requestPriceBytes year month (row:ProvinceCsv.ProvinceCsv.Row) =
        task {
             let cookieContainer = CookieContainer()
             use handler = new HttpClientHandler()
             handler.CookieContainer <- cookieContainer
             use client = new HttpClient(handler)

             let selectMonthUrl = $"http://www.indexpr.moc.go.th/PRICE_PRESENT/Select_month_regionCsi.asp?region={row.RegionId}"
             let! _ = client.GetAsync selectMonthUrl

             let formVals = [ "DDMonth",$"%02i{month}"; "DDYear",$"{year}";
                              "DDProvince", $"{row.Province}"; "texttable",$"{row.TextTable}"
                              "text_name", $"{row.TextName}"; "B1","%B5%A1%C5%A7" ]
                            |> List.map (fun (x,y) -> KeyValuePair<string,string>(x,y))
             use content = new FormUrlEncodedContent(formVals)

             let priceUrl = "http://www.indexpr.moc.go.th/PRICE_PRESENT/table_month_regionCsi.asp"
             return! client
                         .PostAsync(priceUrl, content)
                         .Result.Content.ReadAsByteArrayAsync()
        }

    let private decodeThai bytes =
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
        let thaiEncoding = Encoding.GetEncoding("windows-874")
        let response = thaiEncoding.GetString(bytes, 0, bytes.Length - 1)
        response.ReplaceFirst("windows-874", "utf-8")


    let outputPath year month province =
        $"./data/raw/price/{year}-%02i{month}/{province}.html"

    let downloadPriceFile (logger:ILogger) year month (row:ProvinceCsv.ProvinceCsv.Row) outputPath =
        task {
            let! bytes = requestPriceBytes year month row
            let response = decodeThai bytes

            if response.Contains "error" |> not then
                let file = FileInfo(outputPath)
                file.Directory.Create()
                logger.LogInformation("Writing price file to {OutputPath}", outputPath)
                do! File.WriteAllTextAsync(outputPath, response)
                return true
            else
                let errorMsg = $"Error while requesting price {year}/{month}/{row.Province}/{row.ProvinceNameEn}"
                logger.LogError(errorMsg)
                // logger.LogError("{Response}", response)
                return false
        }

    let alreadyDownloaded year month province =
        outputPath year month province
        |> File.Exists


    type RefreshInterval = int
    type State = {
        YearMonthsQueue: Set<YearMonth>
        RefreshInterval: RefreshInterval
        ProvinceRow: ProvinceCsv.ProvinceCsv.Row
    }
    type Msg =
        | Start of (RefreshInterval * YearMonth list)
        | Refresh

    let initState row =
        let oneHour = TimeSpan.FromHours(1).TotalMilliseconds |> int
        { YearMonthsQueue = Set.empty; RefreshInterval = oneHour; ProvinceRow = row }

    let scrapeMonth (logger: ILogger) (row: ProvinceCsv.ProvinceCsv.Row) (year,month) = async {
        if alreadyDownloaded year month row.Province then
            logger.LogInformation("File of {Year}-{Month}-{Province} already downloaded. Do nothing.",
                                    year, month, row.ProvinceNameEn)
            return Some (year, month)
        else
            let outputPath = outputPath year month row.Province
            let! canDownload = downloadPriceFile logger year month row outputPath |> Async.AwaitTask
            return if canDownload then Some (year,month) else None
    }

    let scrape (logger: ILogger) (row: ProvinceCsv.ProvinceCsv.Row) (yearMonths: YearMonth list) =
        yearMonths
        |> List.map (scrapeMonth logger row)
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.choose id

    type Scraper (logger: ILogger, row:ProvinceCsv.ProvinceCsv.Row)  =
        let agent = MailboxProcessor<Msg>.Start(fun inbox ->
            logger.LogInformation("Province Scraper ready...")

            let rec messageLoop (state: State) = async {
                let! msg = inbox.Receive ()
                match msg with
                | Start (refreshInterval, yearMonths) ->
                    let newQueue = state.YearMonthsQueue + (yearMonths |> Set.ofList)
                    let newState = { state with YearMonthsQueue =  newQueue
                                                RefreshInterval = refreshInterval }

                    inbox.Post Refresh

                    return! messageLoop newState

                | Refresh ->
                    let fetchedYearMonths = scrape logger state.ProvinceRow (state.YearMonthsQueue |> Set.toList)
                    let newQueue = state.YearMonthsQueue - (fetchedYearMonths |> Set.ofArray)
                    let newState = { state with YearMonthsQueue =  newQueue }

                    do! Async.Sleep state.RefreshInterval
                    inbox.Post Refresh

                    return! messageLoop state
            }

            initState row
            |> messageLoop
        )

        interface IScraper with
            member _.Start refreshInterval yearMonths = agent.Post (Start (refreshInterval,yearMonths))

type SectionRow = { Code: string; Desc: string }
type PriceRow = { Code: string; Desc:string; Unit:string; UnitPrice:float }
type MaterialRow =
    | Section of SectionRow
    | Price of PriceRow
    member this.Code =
        match this with
        | Section s -> s.Code
        | Price p -> p.Code
    member this.Desc =
        match this with
        | Section s -> s.Desc
        | Price p -> p.Desc


type Province = int
type State = { Scrapers: Map<Province, IScraper>}
type Msg =
    | Run

type PriceScraper (logger: ILogger<PriceScraper>) =
    do
        logger.LogInformation("building...")

    let bkkScraper = (BkkScraper.Province, BkkScraper.Scraper(logger) :> IScraper)

    let provinceScrapers =
        ProvinceCsv.load ()
        |> List.map (fun row ->
                        let scraper = ProvinceScraper.Scraper(logger, row) :> IScraper
                        (row.Province, scraper))

    let getScrapeYearMonths () =
        let today = DateTime.Now
        [
            today.AddMonths(-1).YearMonth
            today.AddMonths(-2).YearMonth
            today.AddMonths(-3).YearMonth
        ]

    let initState () =
        let allScrapers = bkkScraper::provinceScrapers|> Map.ofList
        { Scrapers = allScrapers }

    let startAllScrapers state =
        let oneDay = 1000 * 60 * 60 * 24
        let threeSec = 1000 * 3
        let oneMin = 1000 * 60
        let refreshInterval = if Env.isDevelopment then oneMin else oneDay
        let yearMonths = getScrapeYearMonths ()
        state.Scrapers
        |> Map.values |> Seq.map (fun sc -> async { sc.Start refreshInterval yearMonths } )
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore

    let agent = MailboxProcessor<Msg>.Start(fun inbox ->
        logger.LogInformation("ready...")

        let rec messageLoop (state: State) = async {
            let! msg = inbox.Receive ()
            match msg with
            | Run ->
                logger.LogInformation("Run....")
                startAllScrapers state

            return! messageLoop state
        }

        initState ()
        |> messageLoop
    )

    member _.Run () =
        logger.LogInformation("Run")
        agent.Post Run
