# Getting Started with Skyline DataMiner DevOps

Welcome to the Skyline DataMiner DevOps environment!
This quick-start guide will help you get up and running. It was auto-generated based on the initial project setup during creation.
For more details and comprehensive instructions, please visit [DataMiner Docs](https://docs.dataminer.services/).

## Creating a DataMiner application package

This project is configured to create a .dmapp file every time you build the project.

When you compile or build the project, you will find the generated .dmapp in the standard output folder, typically the *bin* folder of your project.

When you publish the project, a corresponding item will be created in the online DataMiner Catalog.

## Migrating to a multi-artifact DataMiner application package

If you need to combine additional components in your .dmapp file, you should:

1. Open the `TLRBMT-AS-Alarm List Duration.csproj` file and ensure the `<GenerateDataminerPackage>` property is set to `False`.

1. Right-click your solution and select *Add* > *New Project*.

1. Select the *Skyline DataMiner Package Project* template.

Follow the provided **Getting Started** guide in the new project for further instructions.

## Publishing to the Catalog

This project was created with support for publishing to the DataMiner Catalog.
You can publish your artifact either manually via the Visual Studio IDE or by setting up a CI/CD workflow.

You can adjust the information displayed in the Catalog by modifying the `README.md`, the `manifest.yml`, or the images in the `CatalogInformation` folder.

To add a custom icon, place an image named `custom-icon` in the `images` folder, using one of the supported extensions: `.jpg`, `.jpeg`, `.png`, `.bmp`, `.tif`, `.tiff`, or `.webp`.

### Publishing manually

1. Obtain an **organization key** from [admin.dataminer.services](https://admin.dataminer.services/) with the following scopes:

   - *Register catalog items*
   - *Read catalog items*
   - *Download catalog versions*

1. Securely store the key using Visual Studio User Secrets:

   1. Right-click the project and select *Manage User Secrets*.

   1. Add the key in the following format:

      ```json
      {
        "skyline": {
          "sdk": {
            "dataminertoken": "MyKeyHere"
          }
        }
      }
      ```

1. Publish the package by right-clicking your project in Visual Studio and then selecting the *Publish* option.

   This will open a new window, where you will find a Publish button and a link where your item will eventually be registered.

> [!NOTE]
> To safeguard the quality of your product, consider using a CI/CD setup to run *dotnet publish* only after passing quality checks.

### Changing the version of a package

There are two ways to change the version of a package:

- By adjusting the package version property:

  1. Navigate to your project in Visual Studio, right-click, and select *Properties*.

  1. Search for *Package Version*.

  1. Adjust the value as needed.

- By adjusting the *Version* XML tag:

  1. Navigate to your project in Visual Studio and double-click it.

  1. Adjust the *Version* XML tag to the version you want to register.

     ```xml
     <Version>1.0.1</Version>
     ```
