# Kinsta PHP Monitor 
Kinsta PHP monitor is a cli app you can run to restart the PHP workers if you find specfics errors in the "error" log file.

It uses the Kinsta API to check the logs every X minutes and if it finds new entries with your configured string it will restart the PHP workers using the API.

This is only needed as sometimes long running PHP pages can use up all the workers causing your site to have gateway errors. It is better to get more workers to solve this issue but if it's mostly bots causing the issue this can be a good workaround to keep the site working.

By default, we monitor for the string "upstream timed out (110: Connection timed out)"

## Configuration 

On the first run the app will create a new file `kinsta-monitor-config.json`.
This will hold your API key and environment along with the lookup string and refresh interval.

## Build and Run
To build the app to deploy

```sh
dotnet build
```

To run the app from the code

```sh
dotnet run
```

## License
Kinsta PHP Monitor is licensed under the [MIT license](LICENSE).