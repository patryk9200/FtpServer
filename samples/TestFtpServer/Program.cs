// <copyright file="Program.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement.Directories.RootPerUser;
using FubarDev.FtpServer.AccountManagement.Directories.SingleRootWithoutHome;
using FubarDev.FtpServer.Authentication;
using FubarDev.FtpServer.CommandExtensions;
using FubarDev.FtpServer.Commands;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.DotNet;
using FubarDev.FtpServer.FileSystem.GoogleDrive;
using FubarDev.FtpServer.FileSystem.InMemory;
using FubarDev.FtpServer.FileSystem.Unix;
using FubarDev.FtpServer.MembershipProvider.Pam;
using FubarDev.FtpServer.MembershipProvider.Pam.Directories;
using FubarDev.FtpServer.ServerCommandHandlers;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Mono.Options;

using NLog.Extensions.Logging;

using TestFtpServer.Commands;
using TestFtpServer.Configuration;
using TestFtpServer.Extensions;
using TestFtpServer.Utilities;

namespace TestFtpServer
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var config = new ConfigurationBuilder()
               .AddJsonFile("appsettings.json")
               .AddJsonFile("appsettings.Development.json", true)
               .Build();

            var options = config.Get<FtpOptions>();

            var optionSet = new CommandSet("ftpserver")
            {
                "usage: ftpserver [OPTIONS] <COMMAND> [COMMAND-OPTIONS]",
                { "?|help", "Show help", v => { /* Handled internally by the Options class. */ } },
                "Authentication",
                { "authentication=", "Sets the authentication (custom, anonymous)", v =>
                    {
                        switch (v)
                        {
                            case "custom":
                                options.Authentication |= MembershipProviderType.Custom;
                                break;
                            case "anonymous":
                                options.Authentication |= MembershipProviderType.Anonymous;
                                break;
                            case "pam":
                                options.Authentication |= MembershipProviderType.PAM;
                                break;
                            default:
                                throw new ApplicationException("Invalid authentication module");
                        }
                    }
                },
                "PAM authentication workarounds",
                { "no-pam-account-management", "Disable the PAM account management", v => options.Pam.NoAccountManagement = v != null },
                "Directory layout (system-io, unix))",
                { "l|layout=", "Directory layout", v =>                     {
                        switch (v)
                        {
                            case "default":
                            case "single-root":
                                options.LayoutType = FileSystemLayoutType.SingleRoot;
                                break;
                            case "root-per-user":
                                options.LayoutType = FileSystemLayoutType.RootPerUser;
                                break;
                            case "pam-home":
                                options.LayoutType = FileSystemLayoutType.PamHome;
                                break;
                            case "pam-home-chroot":
                                options.LayoutType = FileSystemLayoutType.PamHomeChroot;
                                break;
                            default:
                                throw new ApplicationException("Invalid authentication module");
                        }
                    }
                },
                "Server",
                { "a|address=", "Sets the IP address or host name", v => options.Server.Address = v },
                { "p|port=", "Sets the listen port", v => options.Server.Port = Convert.ToInt32(v) },
                { "s|pasv=", "Sets the range for PASV ports, specify as FIRST:LAST", v => options.Server.Pasv.Range = v },
                { "promiscuous", "Allows promiscuous PASV", v => options.Server.Pasv.Promiscuous = v != null },
                "FTPS",
                { "c|certificate=", "Set the SSL certificate", v => options.Ftps.Certificate = v },
                { "P|password=", "Password for the SSL certificate", v => options.Ftps.Password = v },
                { "i|implicit", "Use implicit FTPS", v => options.Ftps.Implicit = XmlConvert.ToBoolean(v.ToLowerInvariant()) },
                "Backends",
                new Command("system-io", "Use the System.IO file system access")
                {
                    Options = new OptionSet()
                    {
                        "usage: ftpserver system-io [ROOT-DIRECTORY]",
                    },
                    Run = a => RunWithFileSystemAsync(a.ToArray(), options).Wait(),
                },
                new Command("unix", "Use the Unix file system access")
                {
                    Options = new OptionSet()
                    {
                        "usage: ftpserver unix",
                    },
                    Run = a => RunWithUnixFileSystemAsync(options).Wait(),
                },
                new Command("in-memory", "Use the in-memory file system access")
                {
                    Options = new OptionSet()
                    {
                        "usage: ftpserver in-memory [OPTIONS]",
                        { "keep-anonymous", "Keep anonymous in-memory file systems", v => options.InMemory.KeepAnonymous = v != null }
                    },
                    Run = a => RunWithInMemoryFileSystemAsync(options).Wait(),
                },
                new CommandSet("google-drive")
                {
                    { "b|background|background-upload", "Use background upload", v => options.GoogleDrive.BackgroundUpload = v != null },
                    new Command("user", "Use a users Google Drive as file system")
                    {
                        Options = new OptionSet()
                        {
                            "usage: ftpserver google-drive user <CLIENT-SECRETS-FILE> <USERNAME>",
                            { "r|refresh", "Refresh the access token", v => options.GoogleDrive.User.RefreshToken = v != null },
                        },
                        Run = a => RunWithGoogleDriveUserAsync(a.ToArray(), options).Wait(),
                    },
                    new Command("service", "Use a users Google Drive with a service account")
                    {
                        Options = new OptionSet()
                        {
                            "usage: ftpserver google-drive service <SERVICE-CREDENTIAL-FILE>",
                        },
                        Run = a => RunWithGoogleDriveServiceAsync(a.ToArray(), options).Wait(),
                    },
                },
            };

            if (args.Length == 0)
            {
                await RunFromOptions(options).ConfigureAwait(false);
            }

            return optionSet.Run(args);
        }

        private static async Task RunFromOptions(FtpOptions options)
        {
            options.Validate();
            var services = CreateServices(options);

            switch (options.BackendType)
            {
                case FileSystemType.InMemory:
                    services
                       .Configure<InMemoryFileSystemOptions>(
                            opt => opt.KeepAnonymousFileSystem = options.InMemory.KeepAnonymous)
                       .AddFtpServer(sb => sb.ConfigureAuthentication(options).UseInMemoryFileSystem());
                    break;
                case FileSystemType.SystemIO:
                    services
                       .Configure<DotNetFileSystemOptions>(opt => opt.RootPath = options.SystemIo.Root)
                       .AddFtpServer(sb => sb.ConfigureAuthentication(options).UseDotNetFileSystem());
                    break;
                case FileSystemType.Unix:
                    services
                       .Configure<UnixFileSystemOptions>(opt => opt.Root = options.Unix.Root)
                       .AddFtpServer(sb => sb.ConfigureAuthentication(options).UseUnixFileSystem());
                    break;
                case FileSystemType.GoogleDriveUser:
                    var userCredential = await GetUserCredential(
                            options.GoogleDrive.User.ClientSecrets ?? throw new ArgumentNullException(
                                nameof(options.GoogleDrive.User.ClientSecrets),
                                "Client secrets file not specified."),
                            options.GoogleDrive.User.UserName ?? throw new ArgumentNullException(
                                nameof(options.GoogleDrive.User.ClientSecrets),
                                "User name not specified."),
                            options.GoogleDrive.User.RefreshToken)
                       .ConfigureAwait(false);
                    services
                       .AddFtpServer(sb => sb.ConfigureAuthentication(options).UseGoogleDrive(userCredential));
                    break;
                case FileSystemType.GoogleDriveService:
                    var serviceCredential = GoogleCredential
                       .FromFile(options.GoogleDrive.Service.CredentialFile)
                       .CreateScoped(DriveService.Scope.Drive, DriveService.Scope.DriveFile);
                    services
                       .AddFtpServer(sb => sb.ConfigureAuthentication(options).UseGoogleDrive(serviceCredential));
                    break;
            }

            await RunAsync(services).ConfigureAwait(false);
        }

        private static Task RunWithInMemoryFileSystemAsync(FtpOptions options)
        {
            options.Validate();
            var services = CreateServices(options)
               .Configure<InMemoryFileSystemOptions>(
                    opt => opt.KeepAnonymousFileSystem = options.InMemory.KeepAnonymous)
               .AddFtpServer(sb => sb.ConfigureAuthentication(options).UseInMemoryFileSystem());
            return RunAsync(services);
        }

        private static Task RunWithFileSystemAsync(string[] args, FtpOptions options)
        {
            options.Validate();
            var rootDir =
                args.Length != 0 ? args[0] : Path.Combine(Path.GetTempPath(), "TestFtpServer");
            var services = CreateServices(options)
               .Configure<DotNetFileSystemOptions>(opt => opt.RootPath = rootDir)
               .AddFtpServer(sb => sb.ConfigureAuthentication(options).UseDotNetFileSystem());
            return RunAsync(services);
        }

        private static Task RunWithUnixFileSystemAsync(FtpOptions options)
        {
            options.Validate();
            var services = CreateServices(options)
               .AddFtpServer(sb => sb.ConfigureAuthentication(options).UseUnixFileSystem());
            return RunAsync(services);
        }

        private static async Task RunWithGoogleDriveUserAsync(string[] args, FtpOptions options)
        {
            options.Validate();
            if (args.Length != 2)
            {
                throw new Exception("This command requires two arguments: <CLIENT-SECRETS-FILE> <USERNAME>");
            }

            var clientSecretsFile = args[0];
            var userName = args[1];

            var credential = await GetUserCredential(
                    clientSecretsFile,
                    userName,
                    options.GoogleDrive.User.RefreshToken)
               .ConfigureAwait(false);

            var services = CreateServices(options)
               .AddFtpServer(sb => sb.ConfigureAuthentication(options).UseGoogleDrive(credential));
            await RunAsync(services).ConfigureAwait(false);
        }

        private static async Task<UserCredential> GetUserCredential(
            string clientSecretsFile,
            string userName,
            bool refreshToken)
        {
            UserCredential credential;
            await using (var secretsSource = new FileStream(clientSecretsFile, FileMode.Open))
            {
                var secrets = GoogleClientSecrets.Load(secretsSource);
                credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                        secrets.Secrets,
                        new[] { DriveService.Scope.DriveFile, DriveService.Scope.Drive },
                        userName,
                        CancellationToken.None)
                   .ConfigureAwait(false);
            }

            if (refreshToken)
            {
                await credential.RefreshTokenAsync(CancellationToken.None)
                   .ConfigureAwait(false);
            }

            return credential;
        }

        private static Task RunWithGoogleDriveServiceAsync(string[] args, FtpOptions options)
        {
            options.Validate();
            if (args.Length != 1)
            {
                throw new Exception("This command requires one argument: <SERVICE-CREDENTIAL-FILE>");
            }

            var serviceCredentialFile = args[0];
            var credential = GoogleCredential
                .FromFile(serviceCredentialFile)
                .CreateScoped(DriveService.Scope.Drive, DriveService.Scope.DriveFile);

            var services = CreateServices(options)
               .AddFtpServer(sb => sb.ConfigureAuthentication(options).UseGoogleDrive(credential));
            return RunAsync(services);
        }

        private static async Task RunAsync(IServiceCollection services)
        {
            using (var serviceProvider = services.BuildServiceProvider())
            {
                var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
                loggerFactory.AddNLog(new NLogProviderOptions { CaptureMessageTemplates = true, CaptureMessageProperties = true });
                NLog.LogManager.LoadConfiguration("NLog.config");

                try
                {
                    // Start the FTP server
                    var ftpServerHost = serviceProvider.GetRequiredService<IFtpServerHost>();
                    await ftpServerHost.StartAsync(CancellationToken.None).ConfigureAwait(false);

                    Console.WriteLine("Press ENTER/RETURN to close the test application.");
                    Console.ReadLine();

                    // Stop the FTP server
                    await ftpServerHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }
            }
        }

        private static IServiceCollection CreateServices(FtpOptions options)
        {
            var services = new ServiceCollection()
               .AddLogging(cfg => cfg.SetMinimumLevel(LogLevel.Trace))
               .AddOptions()
               .Configure<AuthTlsOptions>(
                    opt =>
                    {
                        if (!string.IsNullOrEmpty(options.Ftps.Certificate))
                        {
                            opt.ServerCertificate = new X509Certificate2(
                                options.Ftps.Certificate,
                                options.Ftps.Password);
                        }
                    })
               .Configure<FtpConnectionOptions>(opt => opt.DefaultEncoding = Encoding.ASCII)
               .Configure<FubarDev.FtpServer.FtpServerOptions>(
                    opt =>
                    {
                        opt.ServerAddress = options.Server.Address;
                        opt.Port = options.GetServerPort();
                    })
               .Configure<SimplePasvOptions>(
                    opt =>
                    {
                        var portRange = options.GetPasvPortRange();
                        if (portRange != null)
                        {
                            (opt.PasvMinPort, opt.PasvMaxPort) = portRange.Value;
                        }
                    })
               .Configure<PasvCommandOptions>(opt => opt.PromiscuousPasv = options.Server.Pasv.Promiscuous)
               .Configure<GoogleDriveOptions>(opt => opt.UseBackgroundUpload = options.GoogleDrive.BackgroundUpload)
               .Configure<PamMembershipProviderOptions>(opt => opt.IgnoreAccountManagement = options.Pam.NoAccountManagement);

            // Add "Hello" service - unique per FTP connection
            services.AddScoped<Hello>();

            // Add custom command handlers
            services.AddSingleton<IFtpCommandHandlerScanner>(
                _ => new AssemblyFtpCommandHandlerScanner(typeof(HelloFtpCommandHandler).Assembly));

            // Add custom command handler extensions
            services.AddSingleton<IFtpCommandHandlerExtensionScanner>(
                sp => new AssemblyFtpCommandHandlerExtensionScanner(
                    sp.GetRequiredService<IFtpCommandHandlerProvider>(),
                    sp.GetService<ILogger<AssemblyFtpCommandHandlerExtensionScanner>>(),
                    typeof(SiteHelloFtpCommandHandlerExtension).Assembly));

            switch (options.LayoutType)
            {
                case FileSystemLayoutType.SingleRoot:
                    services.AddSingleton<IAccountDirectoryQuery, SingleRootWithoutHomeAccountDirectoryQuery>();
                    break;
                case FileSystemLayoutType.RootPerUser:
                    services
                       .AddSingleton<IAccountDirectoryQuery, RootPerUserAccountDirectoryQuery>()
                       .Configure<RootPerUserAccountDirectoryQueryOptions>(opt => opt.AnonymousRootPerEmail = true);
                    break;
                case FileSystemLayoutType.PamHome:
                    services
                       .AddSingleton<IAccountDirectoryQuery, PamAccountDirectoryQuery>()
                       .Configure<PamAccountDirectoryQueryOptions>(opt => opt.AnonymousRootDirectory = Path.GetTempPath());
                    break;
                case FileSystemLayoutType.PamHomeChroot:
                    services
                       .AddSingleton<IAccountDirectoryQuery, PamAccountDirectoryQuery>()
                       .Configure<PamAccountDirectoryQueryOptions>(opt =>
                        {
                            opt.AnonymousRootDirectory = Path.GetTempPath();
                            opt.UserHomeIsRoot = true;
                        });
                    break;
            }

            if (options.Ftps.Implicit)
            {
                services.Decorate<IFtpServer>(
                    (ftpServer, serviceProvider) =>
                    {
                        var authTlsOptions = serviceProvider.GetRequiredService<IOptions<AuthTlsOptions>>();
                        var sslStreamWrapperFactory = serviceProvider.GetRequiredService<ISslStreamWrapperFactory>();

                        // Use an implicit SSL connection (without the AUTHTLS command)
                        ftpServer.ConfigureConnection += (s, e) =>
                        {
                            TlsEnableServerCommandHandler.EnableTlsAsync(
                                e.Connection,
                                authTlsOptions.Value.ServerCertificate,
                                sslStreamWrapperFactory,
                                CancellationToken.None).Wait();
                        };

                        return ftpServer;
                    });
            }

            return services;
        }
    }
}
