# MechJeb2
Welcome to the branch! The existing readme lives at the bottom of my ramblings (for backwards compatibility of course)
Here, I'll try to create a list of workable items.

# Refinements
Perspective *matters*. That's why I chose to think through the design philosophy of MechJeb on my own devices rather than asking the developers immediately.

Communication *matters*. That's why I chose to communicate.

The maintainers and other developers have brought new light to my design philosophy, and I have pivoted to accommodate.

To understand how the project is structured, you must look at it from the perspective of *piloting mod*. **NOT** *Kerbal piloting mod*.

For this reason I'm writing this to say that the original proposal needs a lot of refinement. I am maintaining my focus on my coding efforts for now.

# Original Proposal
# Core
This needs to be **well defined**. To do this, we need to think about *what it means to be a KSP mod*.

We are bound within the confines of KSP. KSP is our parent. Our source of truth. We are directly coupled with it. We have become one with this environment.

For that reason, I view **Core** as "anything that **needs**" to talk to KSP directly. Anything *outside* of Core should be utilizing Core to talk to KSP, not talking to KSP directly.

However, because this project was not built on that idea [source](https://discord.com/channels/319857228905447436/485125363253641228/1555021580461940747). I must take that into account.

This is where it becomes a monumental effort. Trying to scroll and step through **hundreds, maybe thousands** of files at a glance, each containing **hundreds, maybe thousands** of lines of code.

Weird tangent ass-kissing here, but this project wasn't built on a whimsy. I'm sure many people put a lot of effort into it, The maintainers especially, have honestly done an amazing job putting everything together. MechJeb is easily one of my most frequently used mods and a core feature to my gameplay.

I say this because, a lot of good design decisions went into this, which is what makes this a relatively easy fix... In theory.

Then you realize you're reading everything I just wrote and we haven't gotten anything done!

To recap, we need to *eventually* discuss this, but for now, the issue I have opened is completely insulated from the other projects, so here's what needs to be done.

# High-level design
As mentioned on Discord, Lamont and I have polar opposite views on the direction of MechJebLibBindings. I will make my case here.
MechJeb2 (or a separate, more appropriately named "MechJebCore" or "MechJebKSP" project should be the core. Here is my best effort to describe my vision.

```
Unity/KSP <-> Core <-> Modules <-> Lib
                          |
                          |
    Real Fuels    <- LibBindings ->    FAR
                          /\
                        /    \
                    RP-1      Principia
```

Remember, each of these mods *also lives in our KSP environment*. We are *all* sharing the Unity thread, and currently MechJeb is POUNDING it when the Modules are open.

I will be honest here. I can make no assumptions about the downstream effects of fixing this problem. That is to say, if any consumer has built around the fact that we are feeding them data only 100ms instead of every 20ms, then they must update to accommodate.

All of this is to say "when we're on the main thread, we should do what we **need** to, then pass the thread back as quickly as possible."

# Unix Design Philosophy
Here is where my time from my teens and young adulthood really starts to shine, and why you would read this far.

Let's give credit where credit is due and go over what is correct first.
* Conserve programmer time: Prioritize clean, maintainable, and simple code over clever micro-optimizations or saving machine processing time.

The codebase definitely has structure. Many well named files, functions, and fields. We even have a code design document in here. I recommend adding the documentation from a company that open sources it, like Google or Microsoft. They are quite long, but that's what makes the code bases at those companies more manageable. They are very strict with their design!
* Modularity: Build simple parts connected by clean interfaces.

We don't *actually* implement interfaces in this project from what I've seen. We implement the interface *pattern*, but we do not actually implement interfaces.
  Why do we need interfaces? They don't do anything.. Doesn't that contradict "Conserve Programmer Time"? 
  
  The interface *pattern* is designed for us to view the flow through the *perspective of the consumer*, because they should not see the inner workings of our program when talking to us. They will call us, ask us to do things, and ask us for results. Because the project was built upon this, things have been loosely coupled, and the project could easily grow.
* Do one thing and do it well: Write programs that focus on a single, clear task rather than cluttering software with endless features.

Haha, well, we do have *a lot* of modules in here. However, we have to remember that **KSP is a program begging to be cluttered by said software**.

We launch rockets into orbit and land them on the moon using MechJeb! It *does* do one thing and one thing well, and that's **piloting!**
* Build prototypes early: Create and test working software quickly—within weeks—so you can throw away clumsy designs and rebuild them.
* Work together: Design every program's output to serve as the input for another unknown or future program, typically through text streams and pipes.

#### Now, let's look at where we went wrong
* Silence: Keep programs quiet and uncommunicative when they have nothing surprising or important to say.

  Well, that's the opposite of what we're doing! Some of the modules are *hammering* on the door of MJModStageStats and that's what's causing us to bolt the door down! In this fork, I've started the efforts of feeding them the data through the mailbox built into the door instead.
* Fail noisily: Cause a program to fail early and explicitly when an unexpected error occurs rather than masking the problem

Completely acceptable, we don't want to crash the players game, right? Well, sure, but that doesn't mean we can stop devising solutions! Just because we can't crash the game, doesn't mean we can't pop up a bug report icon when something goes wrong! (I have no idea whether we have this or not. It's the blind leading the blind in this fork.)

This is where I need *your* help, as someone who has made it this far through my ramblings, to share your opinions on the design, naming, and grouping. This allows us to maintain a clear, concise, unified direction.

I can also delegate some tasks for the current open issue, but I fear that would get messy. To quote Linus Torvalds, "Talk is cheap. Show me the code"


-Russell "Russlel" Ford - ToneyBits




# Original MechJeb README.md
Anatid Robotics and Multiversal Mechatronics proudly presents the first flight assistant autopilot: MechJeb

MechJeb2 is a mod for the game Kerbal Space Program. To learn how to use it, [visit the wiki][wiki]. For more
info, [visit this KSP forum post][post].

[wiki]: https://github.com/MuMech/MechJeb2/wiki

[post]: http://forum.kerbalspaceprogram.com/index.php?/topic/154834-122-anatid-robotics-mumech-mechjeb-autopilot-260-12-dec-2016/

## Table of Contents

- [MechJeb2](#mechjeb2)
    - [Table of Contents](#table-of-contents)
    - [Install](#install)
        - [Manual install](#manual-install)
            - [Download](#download)
            - [Unpack](#unpack)
        - [Via CKAN](#via-ckan)
            - [Development version of Mechjeb](#development-version-of-mechjeb)
    - [Common Issues](#common-issues)
    - [Development](#development)
        - [Maintainers](#maintainers)
        - [Code Standards](#code-standards)
        - [Third-party libraries](#third-party-libraries)
        - [Build](#build)
            - [Linux](#linux)
            - [Windows](#windows)
    - [License](#license)

## Install

### Manual install

#### Download

Download from Jenkins:
<https://ksp.sarbian.com/jenkins/job/MechJeb2-Release/>

#### Unpack

Unzip the zip in KSP GameData directory. You should have something that looks like that :

    Kerbal Space Program
    -- GameData
       -- MechJeb2
          -- Bundles
          -- Icons
          -- Localization
          -- Parts
          -- Plugins

### Via CKAN

CKAN has all the release of MechJeb, just install it as usual.

#### Development version of Mechjeb

If you want the unstable dev version of MechJeb then :

1. Open CKAN settings (Settings => CKAN Settings)
2. Press the New button
3. Select the MechJeb-dev line, click OK and exit the options.
4. Refresh
5. Select "Mechjeb2 - DEV RELEASE" in the list
6. Then "Go to Change" to install

## Common Issues

1. Why is the Mechjeb menu not showing?

   Make sure you have the part on your ship (AR202 case in the Control section).

2. (Windows) I cannot find Mechjeb anywhere, there aren't even parts in the R&D facility!

   Some Windows protection and anti-virus software can sometimes block KSP from loading MechJeb.
   You should install KSP outside the `C:\Program Files (x86)\`
   directory. [Steam has an option to change the install directory](https://support.steampowered.com/kb_article.php?ref=7710-tdlc-0426)
   of a game or you can just copy the directory somewhere else.

3. Why is some Mechjeb function not available?

   Science and career mode requires you to unlock some specific node in the Research and Development tree.
   You also may need to upgrade the tracking station to level 2 (game code restriction we can't do much about).

4. How do I report a bug?

   Check if your problem has already been reported: <https://github.com/MuMech/MechJeb2/issues>  
   If you found a problem which is similar to yours, feel free to add more information to the existing issue.

   **If you cannot find the problem**, get
   a [log](https://forum.kerbalspaceprogram.com/index.php?/topic/83212-how-to-get-support-read-first/#Logs) and create a
   new issue with a descriptive title of the problem.

## Development

### Maintainers

- [@sarbian](https://github.com/sarbian)
- [@lamont-granquist](https://github.com/lamont-granquist)

### Code Standards

1. [No var](https://docs.microsoft.com/en-us/visualstudio/ide/reference/convert-var-to-explicit-type): use explicit
   types.
2. Prefer single lines, when possible: especially if-else blocks!
3. [No null-conditional operators](https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/operators/member-access-operators#null-conditional-operators--and-):
   Unity 4.x has
   a [custom == for checking object nulls](https://blog.unity.com/technology/custom-operator-should-we-keep-it).
4. Assembly version needs to remain at 2.5.1.0; file version can be incremented.

### Third-party libraries

- [ALGLIB](https://www.alglib.net/)
- [NSubstitute](https://nsubstitute.github.io/)
- [xunit](https://xunit.net/)

### Build

#### Linux

The project uses Mono and Make to build the addon, make sure you have both installed. You need Nuget to download the external dependencies.

You can also use the [flake.nix](./flake.nix) with direnv or by runnning `nix develop` to set up the development environment.

1. (optional) Set your KSP directory

```sh
export KSPDIR="${XDG_DATA_HOME}/Steam/SteamApps/common/Kerbal Space Program"
```

2. Fetch external packages

```
nuget restore
```

3. Build the mod

```sh
make build
```

4. (optional) Install the mod into your KSP directory

```sh
make install
```

#### Windows

1. Install the version of Unity that KSP uses ( Currently 2019.2.2f1 )

2. Configure your system environment variables and add:

- KSPDIR set to where your KSP install is ( usually **C:\Program Files (x86)\Steam\SteamApps\Common\Kerbal Space Program
  ** )
- MONO set to the path of Unity current mono.exe ( usually C:\Program
  Files\Unity\Hub\Editor\2019.2.2f1\Editor\Data\MonoBleedingEdge\bin\mono.exe )
- PDB2MDB set to the path of pdb2mdb.exe ( usually **C:\Program
  Files\Unity\Hub\Editor\2019.2.2f1\Editor\Data\MonoBleedingEdge\lib\mono\4.5\pdb2mdb.exe** )

3. Load MechJeb2.sln and open the properties of the MechJeb2 project (Right-Click=>properties). In the "Reference Path"
   section add the KSP libs folder to the list ( usually **C:\Program Files (x86)\Steam\SteamApps\Common\Kerbal Space
   Program\KSP_x64_Data\Managed** )

4. Repeat step 3 for the MechJebLib, MechJebLibBindings, and MechJebLibTest projects.

5. Perform `nuget restore` to get external dependencies such as JetBrains.Annotations.

## License

Licensed under the [GNU General Public License, Version 3](LICENSE.md).

Portions (in the "MechJebLib" directory) are placed in the public domain and are documented in
the affected source code headers.
