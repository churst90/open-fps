# Door event structure: knob/lever doors with spring latches, and steel stair doors with push bars and closers

Scope: what physically happens, event by event, when a residential latched door and a commercial steel door with an exit device and hydraulic closer are opened and closed; the timings, travels, forces and speeds that set the spacing of the sounds. Standards text (BHMA A156.3 draft, LCN installation sheet, SDI) was read directly where possible. Many timing figures for the latch events themselves are not published anywhere I could find; those are derived in the Inferences sections from published geometry and speeds, and marked as derived.

## 1. Closing a latched door: bevel, snap, stop, rebound, keeper, settle

### Takeaway
The published geometry is small: a residential tubular latch throws 1/2 in (12.7 mm), an exit device latch 3/4 in (19 mm). The door face sits 1/16 to 3/32 in (1.6 to 2.4 mm) off the stop, and the latch face sits within 1/8 in (3.2 mm) of the strike. Because those gaps are millimetres, the time between "latch snaps out" and "leaf hits stop" depends entirely on edge speed. It is tens of milliseconds when a closer's latch speed brings the door in (about 0.05 to 0.1 m/s at the edge), and it falls to about a millisecond or less on a slam, so the click and the thump fuse. Nobody publishes measured building-door slam speeds; the only measured or quoted slam figures I found are for car doors.

### Cited Findings
**Latch geometry and fit**
- A standard tubular latch has a 1/2 in throw, and it can be extended up to 1/8 in in the field to take up a larger gap — [Lowe's/Accurate Lock search summary; Accurate Lock tubular latch](https://accuratelockandhardware.com/product/tubularlatch/) (vendor; the 1/2 in residential throw is the industry norm).
- Fire-door latch throw is set by the door's listing, typically 1/2 in to 3/4 in. Recent NFPA 80 editions dropped the numeric throw from the body text — [cdfdistributors NFPA 80 guide](https://www.cdfdistributors.com/blog/post/nfpa-80-fire-door-requirements); [ufiredoors](https://ufiredoors.com/blog/fire-door-wont-latch/) (secondary sources).
- Von Duprin 99 rim exit device: "Deadlatching, 3/4" throw" latchbolt; 299 roller strike standard; centre case 8 in x 2-3/4 in x 2-3/8 in — [TruDoor 99EO listing](https://www.trudoor.com/products/von-duprin-99eo-panic-rim-exit-device).
- Strike reveal: the gap between latch face and strike face should be at most 1/8 in. Adjustable strikes stop rattling and make sure the deadlatch engages. If the auxiliary deadlatch plunger falls into the strike hole along with the latchbolt, deadlatching fails — [iDigHardware, Strike Latch Guard](https://idighardware.com/2013/10/ff-strike-latch-guard/) (via search summary).
- In the BHMA A156.3 deadlatching test, 1/8 in (3.2 mm) is added to the manufacturer's strike-clearance dimension. The device must still deadlatch over 5 cycles. In other words, 1/8 in of extra strike slop is the tolerance window the standard designs for — [BHMA A156.3 draft, §89.6](https://buildershardware.com/LinkClick.aspx?fileticket=kJ3qTCDM_Ew%3D&portalid=0).
- Force to latch (exit devices): a gauge 1 in from the lock edge, starting "when the door is just clear of the latch contacting the lip of the strike", closes the door slowly until the latch fully engages. The maximum is 4.5 lbf (20 N) for all grades — [BHMA A156.3 draft, §89.7](https://buildershardware.com/LinkClick.aspx?fileticket=kJ3qTCDM_Ew%3D&portalid=0). BHMA A156.2 (bored locks) has an equivalent "force to latch door" test with the same set-up, gauge 1 in from the lock edge on the latch centreline; the limit value was not visible in the sources I could reach — [ANSI blog on A156.2-2022](https://blog.ansi.org/ansi/ansi-bhma-a156-2-2022-bored-locks/).
- Anti-rattle strikes have a tab you bend or screw-adjust to take up latch-to-strike slop. Bending the strike tab is the standard fix for a door that rattles when closed — [Carlisle Brass install guide](https://www.carlislebrass.com/media/fitting/file/FITES0098d.pdf); [search summary of DIY sources](https://windowhardwaredirect.com/blogs/news/how-to-adjust-door-latch-length-for-optimal-security-and-functionality).

**Clearances that set the rattle and stop gap**
- Steel door in a hollow metal frame: door-to-frame clearance at most 1/8 in; meeting edges of pairs 3/16 in; undercut at most 3/4 in (19.1 mm) — [SDI FAQ](https://steeldoor.org/faqs/); [ANSI/SDI A250.8 via search](https://steeldoor.org/wp-content/uploads/2020/02/A250_8-1.pdf).
- Clearance between the door face and the stop: 1/16 in (1.6 mm) to 3/32 in (2.4 mm) — [ANSI/SDI A250.8-2023 via search summary](https://steeldoor.org/wp-content/uploads/2020/02/A250_8-1.pdf).
- Wood interior doors: about 1/8 in (3 mm) at the top and latch side, 1/2 to 3/4 in at the bottom. Latch edges are bevelled about 5° (often quoted as 1/8 in in 2 in) so the leading edge clears the jamb on its arc — [search summary of carpentry sources, e.g. Family Handyman](https://www.familyhandyman.com/project/how-to-shim-gapping-doors/) (secondary).
- Silencers (rubber mutes in the stop) give a 1/16 to 1/8 in cushion. They hold the door snug so the latch does not rattle from air-pressure changes or traffic vibration, and "provide constant tension for door latches" — [Reflect Window product page](https://www.reflectwindow.com/products/commercial-steel-door-frame-mute-for-quieter-closures); [RBA Door](https://www.rbadoor.com/door-silencer-for-metal-frames.html) (vendor pages).

**Closing speeds (all sources found are automotive or closer settings)**
- Automotive patent: a user gives a door at least 0.33 rpm (2°/s) over the last 5° to close it, and 5 rpm (30°/s) to 15 rpm (90°/s) "is considered slamming". The patent's slowdown from slam speed to soft-close speed takes about 200 to 300 ms. NOTE: this patent is about car doors ("a device for use on an automotive vehicle door") — [US10392849B2](https://patents.google.com/patent/US10392849B2/en).
- Car doors close at about 0.8 to 1.2 m/s at the latch (measured benchmarks 0.79 to 1.03 m/s). Car doors need this speed to compress their seals, so it is an upper-normal figure, not a slam — [Numerical analysis of door closing velocity](https://airccse.org/journal/ijci/papers/4215ijci01.pdf).
- Closer-controlled building doors: 90° to 12° in at least 5 s (ADA 404.2.8), then a slower latch zone (see section 4) — [doorclosersusa ADA 404.2.8](https://www.doorclosersusa.com/ADA-Door-Closing-Speed-ADA-404-2-8-and-404-2-9-Door-Clo-s/36251.htm).

**Order of sounds (Foley practice)**
- Sound-design guides describe a closing door as a high-frequency transient (latch against strike), then a low/mid "body" thump from the leaf (heavy door deep, hollow-core "thin and boxy"), then a tail of reverberation and rattle. They recommend staggering the latch click "first" and the thump "almost immediately after" by a few ms — [sfxengine door slam blog](https://sfxengine.com/blog/door-slam-sound-effect). LOW-QUALITY SOURCE (AI-tool vendor blog). It agrees with the derived timing below, but it is not evidence on its own.

### Inferences
These are derived from the cited geometry; the numbers are mine.
- **Event sequence, latched door closing (one swing):**
  1. *Leaf swing.* Little sound apart from hinge friction and air.
  2. *Bevel contact.* The bevel of the latch (and, on a deadlatch, the guard plunger beside it) meets the strike lip. The bolt is cammed back against its spring and scrapes or slides over the lip. The leaf has to travel about the bevel depth, roughly the throw, in the door-normal direction during this. Duration ≈ throw / edge speed: 12.7 mm at 0.06 m/s (closer latch speed) is about 200 ms of soft scrape; at 0.25 m/s (a normal hand push) about 50 ms; at 2 m/s (a slam) about 6 ms. On a deadlatch the guard plunger is pushed in by the strike face and stays in, adding a small second tick.
  3. *Snap.* The bolt tip passes the lip and the spring fires the bolt out into the strike pocket. The click is the bolt's head or shoulder hitting its own stop in the latch case, possibly also the strike box. The bolt is a few tens of grams moving about 12 mm under a spring force of a few newtons (A156.3 caps force-to-latch at 20 N for exit devices). So the spring-out probably takes on the order of 5 to 15 ms (rough estimate, not measured).
  4. *Leaf meets stop.* The leaf meets the stop or silencers after a further 1.6 to 3 mm (stop clearance or silencer projection, or the remaining strike-pocket slop). Latch-click-to-thump gap ≈ 2 mm / edge speed: about 30 ms at 0.06 m/s, about 8 ms at 0.25 m/s, and about 1 ms at 2 m/s. On a slam the leaf can reach the stop before the bolt has finished springing out (step 3 takes 5 to 15 ms), so the order can invert: thump first, click inside or after the thump.
  5. *Rebound.* The leaf bounces off the stop or silencers back toward the open side by up to the strike clearance (≤ 1/8 in, 3.2 mm). The flat face of the bolt then strikes the keeper face of the strike: a second, sharper metallic clack, the "latch catching the door". It comes one rebound half-period after the thump. That is the silencer or weatherstrip spring and leaf-mass bounce time, which I could not find published; plausibly 10 to 50 ms.
  6. *Settle / jiggle.* Several smaller bounces between stop (or silencer) and keeper, decaying. Their spacing is set by the clearance, and their number by the damping: rubber silencers or weatherstrip leave one or two; bare wood or metal stops with 1/8 in slop give a short rattle. Hinge-knuckle clearance and loose hinge screws add looser rattles of the leaf as a whole.
  7. *Air.* A fast-closing leaf pushes a pressure pulse into the room. Hollow-core doors and their frames may "breathe" after the slam.
- With a well-adjusted strike (latch snug against the keeper and the leaf against the silencers), steps 5 and 6 nearly vanish: no rebound clack, just the click and thump. With slop up to 1/8 in, the rebound clack and the rattle appear. That makes strike clearance a direct, physical parameter for the "jiggle".

### Gaps
- No measured edge speeds for building doors closed by hand (gentle, normal, slammed). The 30 to 90°/s "slam" band is from a car-door patent. Converted to building doors (latch about 0.8 to 0.9 m from the hinge axis) it gives 0.4 to 1.4 m/s. That is plausible but unverified, and an angry slam may well be faster.
- No published bevel angles, latch spring forces or bolt masses for residential tubular latches, and the A156.2 force-to-latch limit was not reachable. The 5 to 15 ms bolt spring-out time is a rough estimate.
- No published rebound or settle times, and no stiffness figures for silencers or weatherstrip.

## 2. Opening a knob or lever door

### Takeaway
A knob or lever turns roughly 40 to 65° to retract the latch fully; one patent cites 52° for a conventional entrance lock and about 60° for another design. The release torque allowed after the standards' abuse tests is 18 lbf·in (2 Nm) for a knob and 50 lbf·in (5.7 Nm) for a lever, so levers are sprung harder: a lever needs its own return spring to hold it level, while a knob relies on the latch spring alone. Opening produces a short series of sounds: lost-motion (spindle play) tick, spring wind-up, the bolt sliding back and off the keeper face (dragging if the door is pressed against it), the leaf moving. Then, on release, the spring return "snap" or clack of the trim and bolt.

### Cited Findings
- One conventional entrance lock needs at least 52° of lever rotation to fully retract the latch; another latch needs about 60°, with the hub able to go to 65° (over-retracted). A 52° lever turn rotates a cam about 38° and pivots a latch member about 39° — [Small-angle latch patent US11585115](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/11585115) (via search summary).
- BHMA A156.3 trim, maximum torque to release the latch after the abuse tests: knob 18 lbf·in (2 Nm), lever 50 lbf·in (5.7 Nm), turn 27 lbf·in (3 Nm), thumb piece 18 lbf (80 N) — [BHMA A156.3 draft, §10–11 tables](https://buildershardware.com/LinkClick.aspx?fileticket=kJ3qTCDM_Ew%3D&portalid=0).
- Lever-operated locks must open with a maximum torque of 28 in·lbf (A156.2, as summarised) — [ANSI blog A156.2](https://blog.ansi.org/ansi/ansi-bhma-a156-2-2022-bored-locks/).
- "Double-sprung" tubular latches have two springs: one in the follower or handle side, which sets handle resistance and returns the lever to horizontal, and one that returns the bolt. "Heavy sprung" latches carry a stronger return spring for heavy or unsprung levers. A lever spring assist exists to stop lever sag and give "the 'snap' of a good spring return" — [More Handles blog](https://www.morehandles.co.uk/blog/more-handles-how-to-technical-guides-what-is-a-tubular-latche-how-to-choose-the-correct-tubular-latch-for-your-project/); [Carlisle Brass heavy sprung latch](https://www.carlislebrass.com/heavy-sprung-tubular-latch-51126); [FPL lever spring assist](https://fplhardware.com/product/lever-spring-assist-w-tubular-latches/).
- Von Duprin's quiet trim (QM 996) uses a "damper-controlled lever return" because the lever's spring return is itself a noise source. Its patent family also lists rattle from clearance fits and metal-on-metal impacts in trim — [Von Duprin Quiet Mechanical](https://www.vonduprin.com/en/products/options-accessories/quiet-monitoring.html); [US11220838B2](https://patents.google.com/patent/US11220838B2/en).
- In hospitals, a conventional door latch and handle "may generate over 30 additional decibels" above a 40 to 42 dB ambient when the door is opened. Low-energy quiet latches reach "as low as 55 dB" — [Locksmith Ledger, "Sshhh!"](https://www.locksmithledger.com/home/article/12007597/problem-solver-sshhh-quiet-is-a-consideration-for-healthcare-facilities); search summary of patents [US10844637](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/10844637).

### Inferences
- **Event sequence, opening a latched knob door:**
  1. *Grip and lost motion.* The first several degrees of the turn take up spindle-to-hub play. This makes small ticks of the square spindle against the follower, and on cheap sets the rose or knob rattles.
  2. *Wind-up.* The follower cams the bolt back against the latch spring. The spring creaks or ticks and the bolt slides. If the door is pressed toward the stop (by a closer, weatherstrip or silencers), the flat face of the bolt drags across the keeper lip under load: a scrape or grind, louder with more preload.
  3. *Release from keeper.* When the bolt clears the keeper, the leaf jumps the preload distance (up to the silencer compression or weatherstrip, about 1 to 3 mm) with a small knock. Weatherstripped exterior doors give an air "unstick" as well.
  4. *Leaf moves.* Hinge sounds.
  5. *Handle released.* If released with the door open, the bolt shoots out to full throw and hits its internal stop: a clack, the "latch spring return clack". At the same moment the lever or knob returns and hits its rose stop: a second clack within a few ms. Levers with stronger return springs and more mass make a heavier clack than knobs. If the user lets the handle back slowly, these become soft ticks.
- Turning by 40 to 65° at a normal wrist speed takes a few hundred ms, roughly 0.2 to 0.5 s (estimate). The drag in step 2 lasts as long as the bolt is moving under load.

### Gaps
- No published spindle-play angles, latch spring rates or bolt masses for residential tubular latches.
- No published timing of handle turns.

## 3. Push-bar (panic / exit) devices: travel, force, internals, the "ka-chunk", quiet versions

### Takeaway
A push pad moves about 3/4 in (19 mm) on a Von Duprin 99: projection 3-13/16 in at rest, 3-1/16 in depressed. It must retract the latch with no more than 15 lbf (67 N) under ANSI/BHMA A156.3, or 80 N under EN 1125. Inside the case, the pad drives bell cranks that move a drive bar against a return spring; the drive bar pulls the latch retractor. Rim, mortise and vertical-rod devices add the latch and, for rod devices, top and bottom bolts on rods. The loud "ka-chunk" is a chain of metal-on-metal end-stops:
- push: pad bottoming, crank and drive-bar stops, latch retracting
- release: pad snapping back, drive bar and retractor hitting their rest stops, the latch shooting out

The hollow case and rail radiate all of it, and on rod devices the rods rattle. Quiet devices put fluid dampers on the pad's return and on re-latching, plus bumpers, grease and elastomer washers at the contact points. Those parts remove exactly those impacts.

### Cited Findings
**Travel and force**
- Von Duprin 99: push-pad neutral projection 3-13/16 in, depressed 3-1/16 in (difference 3/4 in = 19 mm); 3/4 in throw deadlatching latchbolt; half-turn hex dogging; centre case 8 x 2-3/4 x 2-3/8 in — [TruDoor 99EO](https://www.trudoor.com/products/von-duprin-99eo-panic-rim-exit-device).
- A156.3 §89.2.1: with the door latched, the actuating bar shall be depressed by a force not exceeding 15 lbf (67 N) until the latch clears the strike. Measured at the centre and 1.5 in (38 mm) from each end. All grades — [BHMA A156.3 draft](https://buildershardware.com/LinkClick.aspx?fileticket=kJ3qTCDM_Ew%3D&portalid=0).
- A156.3 §89.2.2: with 250 lbf (1110 N) pushing the door outward (people pressing on a locked door), release must take no more than 50 lbf (220 N) — [BHMA A156.3 draft](https://buildershardware.com/LinkClick.aspx?fileticket=kJ3qTCDM_Ew%3D&portalid=0).
- A156.3 §89.5: if a depressed crossbar leaves clearance to the door, it must be at least 1 in (25 mm). No gap may trap a 0.375 in (10 mm) rod at any point in the bar travel — [BHMA A156.3 draft](https://buildershardware.com/LinkClick.aspx?fileticket=kJ3qTCDM_Ew%3D&portalid=0).
- A156.3 test set-up: 3 ft x 7 ft x 1-3/4 in (915 x 2134 x 45 mm) doors in metal frames; a closer, if fitted, is set to at most 8 lbf (36 N) closing force at 30 in (762 mm) from the pivot — [BHMA A156.3 draft §7](https://buildershardware.com/LinkClick.aspx?fileticket=kJ3qTCDM_Ew%3D&portalid=0).
- EN 1125 activation force is limited to 80 N (18 lbf). F12 is the release force under a 1000 N door load. A vendor blog claims full retraction within 20 to 25 mm of bar travel — [yunngd blog](https://www.yunngd.com/2026/02/28/emergency-exit-hardware-global-variations-and-field-installation-risks/) (vendor blog, unverified); [iTeh EN 1125 abstract](https://standards.iteh.ai/catalog/standards/sist/6a01fa68-fc90-4906-abd0-058dcb7038ee/sist-en-1125-2008). I could not see the 220 N under-load limit in EN 1125 itself; 220 N / 50 lbf is the A156.3 figure above.
- NFPA 101 egress door forces: at most 15 lbf (67 N) to release the latch, 30 lbf (133 N) to set the leaf in motion, and 15 lbf (67 N) to open it to the required width, applied at the latch stile — [UpCodes, door leaf operating forces](https://up.codes/s/door-leaf-operating-forces); [BHMA summary PDF](https://buildershardware.com/Portals/0/Door%20Unlatching%20Maximum%20Operating%20Forces,%20and%20Maximum%20Door%20Leaf%20Operating%20Forces%20_.pdf).

**What is inside**
- Allegion patents describe the internals: a pushbar on brackets; bell cranks linking the pushbar to a drive bar; a return spring on the drive bar biasing it to the de-actuated state; a latch control assembly; retractor, drive pin, control link, header bracket and base plate. For vertical rod devices, connector or control links run to the top and bottom latches — [Latchbolt damping module US11156025B2](https://patents.google.com/patent/US11156025B2/en); [US11220838B2](https://patents.google.com/patent/US11220838B2/en); search summary of the same family.
- Bumpers engage when the pushbar is about 0.1 in (0.085 to 0.115 in) from fully depressed, to cushion bottoming — [US11220838B2](https://patents.google.com/patent/US11220838B2/en).

**Why it is loud**
- The noise sources the patents list are latchbolt movement and impact on the strike; "vibrations resulting from contact between components (e.g. … impact and/or sliding engagement)"; rattle from clearance fits; metal-on-metal impacts; and links and assemblies returning to rest under spring force — [US11220838B2](https://patents.google.com/patent/US11220838B2/en).
- On latchbolt return: the "free return of the latchbolt from its retracted position to its extended position", driven by the return spring, makes parts "impact or grind against one another" — [US11156025B2](https://patents.google.com/patent/US11156025B2/en).
- Conventional panic devices have "complicated mechanisms" that "generate significant impact or operating noise caused by the operation of the push bar". Measured "29 to about 35 decibels" above a 40 to 45 dB hospital ambient (so about 70 to 80 dB). The patented dampened device measured "0.01 decibels" above ambient (patent claim, likely optimistic). Damping went on washers at pivot pins, pads on the bar edge, grommets where the bar meets the push arm, and pads in the lever-arm and latch-lock parts — [Quiet panic device US10907377B2](https://patents.google.com/patent/US10907377B2/en).
- Hospital practice names "the clanking and banging associated with mechanical push pads on exit devices" and "rattles and clanking of rods in the door". The fixes are concealed vertical cables instead of rods, and dampers that "decelerate the mechanical push pads on the push and return stroke" — [Locksmith Ledger](https://www.locksmithledger.com/home/article/12007597/problem-solver-sshhh-quiet-is-a-consideration-for-healthcare-facilities).

**Quiet versions**
- Von Duprin 98/99: "The Quiet One" fluid dampener in a grooved mechanism case decelerates the push pad on its return stroke, "so the pad settles back without the metallic snap you get from an undamped device". It is fitted to all 98/99 devices — [search summaries of Von Duprin and dealer pages](https://www.vonduprin.com/en/products/options-accessories/quiet-monitoring.html).
- Von Duprin QM (Quiet Mechanical): damper-controlled re-latching on 98/99 and 33A/35A devices, and a damper-controlled lever return on 996 trim. Von Duprin says overall quiet also needs "a properly adjusted door closer and door silencers" and depends on door and frame material — [Von Duprin QM page](https://www.vonduprin.com/en/products/options-accessories/quiet-monitoring.html).
- QEL: motor-driven latch retraction paired with a hydraulic damper, quieter than solenoid retraction — [Von Duprin QEL](https://www.vonduprin.com/en/products/options-accessories/quiet-latch-retraction.html).
- Damping methods in the patents: fluid dampers with plungers; rotary hydraulic dampers (a rack and pinion slowing latchbolt extension); spring-biased slowing arms; damping grease on pivots; low-hardness plastic or rubber bumpers; guide channels; spring tabs against trim rattle — [US11220838B2](https://patents.google.com/patent/US11220838B2/en); [US11156025B2](https://patents.google.com/patent/US11156025B2/en).

### Inferences
- **Event sequence, pushing a rim exit device on a closer-held steel door:**
  1. *Hand hits pad.* A palm slap on a sheet-metal or aluminium pad.
  2. *Push stroke, about 19 mm.* Bell cranks rotate, the drive bar slides against its return spring, and the latch retractor pulls the 3/4 in latch back out of the strike. If people are already leaning on the door, the latch drags against the keeper under load; the A156.3 test assumes up to 1110 N of door preload. The pad bottoms on its stops, or on bumpers 0.1 in before the end on quiet units. Together these give the "ka" (stroke and retract) and the "chunk" (bottoming), all radiated by the hollow case and rail. Stroke time at a brisk push is about 50 to 150 ms (estimate: 19 mm at 0.1 to 0.4 m/s).
  3. *Door released from strike.* With the latch out, closer preload and silencer compression let the leaf jump a millimetre or two. Then the push swings it.
  4. *Pad released.* While the person passes through, the hand comes off. On an undamped device, the return spring snaps pad, cranks and drive bar back to rest, and the latch shoots out to full throw with nothing in front of it. The result is a second loud clack, the "metallic snap". On a Quiet One or QM device this becomes a slow, damped glide of a few hundred ms with little impact. (The duration is my guess; no published figure.)
  5. *Closer returns the door.* Section 4.
  6. *Re-latching on a rim device.* A roller strike (Von Duprin 299) or a bevelled latch rides the strike. The latch, or on deadlatching devices the latch and its auxiliary trigger, gets pushed in and snaps out into the strike, with the latch and keeper rebound described in section 1. On vertical rod devices the top latch meets the head strike and the bottom latch meets the floor strike. The rods' slack lets them rattle in the door and frame, and top and bottom latches may not fire at exactly the same instant.
- Dogged devices (pad held down with a hex key) do not retract or extend the latch on each use. They give only the leaf and closer sounds, plus a softer pad tap.

### Gaps
- No published dB spectra or time histories for an exit device's push and return impacts. The 29 to 35 dB above ambient figure is from a patent and the conditions are unclear.
- No published pad return time for dampened against undamped devices.
- Mortise-lock exit devices were not separately documented: the latch sits in a mortise lock in the door edge, driven by the device. Neither were the specific sounds of concealed vertical rod devices compared with surface ones (concealed rods rattle inside the hollow door).
- EN 1125 bar travel (20 to 25 mm) comes only from a vendor blog.

## 4. Door closers and steel frames: speeds, times, latching, slam, silencers, grout

### Takeaway
A hydraulic closer gives a door two closing phases. First a sweep from about 90° down to about 10 to 15°: ADA requires at least 5 s from 90° to 12°, and LCN calls 5 to 7 s total "normal", split about evenly between sweep and latch. Then a separately valved latch zone over the last 10 to 15° (about 8 in of edge travel) whose speed is set so the door "clicks" rather than slams. Backcheck cushions opening from about 70°. EN 1154 closers must be adjustable to 3 s or less and 20 s or more. A latch valve opened too far is the usual cause of the commercial-door slam. Silencers (3 per strike jamb) set a 1/16 to 1/8 in cushion between leaf and stop. They are what takes the metal-on-metal bang out of a steel door meeting a steel frame. SDI does not recommend grouting frames (except as sound deadening in masonry), so many steel frames are hollow.

### Cited Findings
- ADA 404.2.8: door closers adjusted so the door takes at least 5 s to move from 90° open to 12° from the latch — [doorclosersusa ADA 404.2.8](https://www.doorclosersusa.com/ADA-Door-Closing-Speed-ADA-404-2-8-and-404-2-9-Door-Clo-s/36251.htm); [Construction Specifier](https://www.constructionspecifier.com/achieving-ada-compliance-for-door-hardware/5/).
- Commercial closers have separate valves for closing (sweep), latching and backcheck. Sweep runs from fully open to "approximately 8 inches from closed"; latch speed covers the final 8 in. Too slow and the door fails to latch, too fast and it slams. Backcheck starts at about 70° of opening and does not affect closing — [doorclosersusa ADA guide](https://www.doorclosersusa.com/ADA-Door-Closer-Requirements-s/34426.htm); [Doorways Plus](https://www.doorwaysplus.com/blog/our-blog-1/getting-the-closer-right-for-ada-opening-force-sweep-time-and-the-adjustments-that-actually-matter-514).
- Latch speed is "the rate in the last 10 to 15 degrees of closing arc". Delayed action "slows sweep speed dramatically for roughly the first half of its range" — [Wikipedia, Door closer](https://en.wikipedia.org/wiki/Door_closer).
- LCN 4040XP installation sheet: "a normal closing time from 90° open position is 5 to 7 seconds, evenly divided between main speed and latch speed". Valves: 1 backcheck, 2 main speed, 3 latch speed, 4 delay speed (4041 DA). Hold-open 90° to 120° option. Spring power adjusted to a chart, e.g. "34 in, 8.5 lbf" in the door-width table — [LCN 4040XP pull-side instructions](https://us.allegion.com/content/dam/allegion-us-2/web-documents-2/InstallInstructions/LCN_4040XP_Series_Pull_Side_Mount_Installation_Instructions_107160.pdf). A secondary summary gives about 3 to 4 s sweep and 3 s latch — [securityparts](https://www.securityparts.com/how-to-adjust-commercial-door-closer).
- EN 1154: closing time from 90° must be adjustable to 3 s or less and 20 s or more. Latch action adjusts the last about 10° "to overcome a latch or intumescent seal". Power sizes run from 1 to 7 against door width and test mass, sizes 1 to 3 covering doors up to 950 mm and 60 kg. Endurance is 500,000 cycles from 90°. One summary quotes closing moments at 0 to 4° of about 18.9 to 19.5 Nm max, and a minimum at other angles of about 4.7 to 5.1 Nm, but does not say which power size — [iTeh EN 1154 abstract](https://standards.iteh.ai/catalog/standards/cen/00f1b7c2-9cd5-4ba3-be93-e05fde567005/en-1154-1996); [Hoppe EN 1154](https://www.hoppe.com/in-en/contacts-service/standards/bs-en-1154/) (search summary; the power-size table itself was blocked, HTTP 403).
- Opening force: NFPA 101 15/30/15 lbf (section 3). ADA interior doors without closers: 5 lbf max — [UpCodes](https://up.codes/s/door-leaf-operating-forces).
- Closer noises named in troubleshooting guides: hissing (oil through valves, adjust sweep or spring), squeaking (dry arm joints), popping or knocking (worn or loose arm screws). "If it closes fine until the last few inches and then slams, the latch speed is probably too fast" — [McCoymart closer troubleshooting](https://mccoymart.com/post/mastering-door-closer-issues-troubleshooting-guide/); [locksmithbc](https://locksmithbc.com/door-closer-adjustment-guide/) (secondary, practitioner-level).
- Silencers: rubber mutes in 9/32 in holes in the stop of hollow metal frames, typically 3 on the strike jamb of a single door (pairs use the head). They absorb the leaf's impact, prevent "metal-on-metal contact", eliminate rattle and keep tension on the latch — [Reflect Window](https://www.reflectwindow.com/products/commercial-steel-door-frame-mute-for-quieter-closures); [RBA Door](https://www.rbadoor.com/door-silencer-for-metal-frames.html); [iDigHardware, Empty Silencer Holes](https://idighardware.com/2024/02/qq-empty-silencer-holes/).
- Hospitals use heavy-duty rubber silencers that "absorb the force generated from closing the door, making it virtually silent" — [Locksmith Ledger](https://www.locksmithledger.com/home/article/12007597/problem-solver-sshhh-quiet-is-a-consideration-for-healthcare-facilities).
- Grout: SDI believes "there are more risks than rewards when grouting frames". Grouting can help sound deadening in masonry walls but is not recommended in drywall; "a properly anchored frame without grout can pass fire and hose stream, cycle, and even impact tests" — [SDI FAQ](https://steeldoor.org/faqs/). Practitioners recommend proper anchorage and silencers as the alternative to grout for deadening — [4specs forum](http://discus.4specs.com/discus/messages/7869/5927.html) (forum).

### Inferences
- **Kinematics of a closer cycle (36 in leaf, latch radius about 0.88 m):**
  - Sweep from 90° to about 12° in 2.5 to 4 s gives about 20 to 30°/s average, so 0.3 to 0.45 m/s at the latch edge.
  - The latch zone of about 12° over about 2.5 to 3.5 s gives about 3 to 5°/s, so about 0.05 to 0.08 m/s at the edge.
  - At that latch speed, the bevel ride over 19 mm (3/4 in exit latch) lasts about 0.25 to 0.4 s, and the gap from latch snap to silencer contact is about 20 to 50 ms. A properly set closer therefore gives a slow scrape, a distinct click, then a soft muted landing, not a bang.
- **Slam with a closer:** a mis-set latch valve (or a person pulling the door shut) lets the door reach the frame at sweep speed or faster, roughly 0.3 to 1 m/s or more. Click and landing then fuse into one hit. A steel leaf of about 30 to 35 kg (section 5) landing on silencers or a bare steel stop excites the hollow frame and leaf, and with no silencers the metal-on-metal contact rings.
- **Backcheck** produces no impact on opening, just hydraulic resistance (a faint hiss or whoosh of oil). It is what stops a flung-open door hitting the wall.
- **Delayed action** holds the door near full open for several seconds before the sweep begins; a fire door can sit open and then start to close.
- **Arm linkage:** the knuckle and forearm joints are a source of squeak, and tick or knock if worn. A parallel-arm (push-side) closer's arm folds against the frame head.

### Gaps
- No published sound-level measurements of closer-controlled latching compared with a slam.
- The EN 1154 power-size closing-moment table could not be retrieved (403). From memory, unverified: sizes 1 to 7 run from about 9 Nm to 87+ Nm at 0 to 4°. Treat that as unconfirmed.
- No published acoustic data on grouted against ungrouted frames (only the qualitative "sound deadening" claim).

## 5. Leaf construction: hollow-core and solid-core wood, hollow metal steel

### Takeaway
A hollow-core interior door is two 1/8 to 1/4 in hardboard or MDF skins on a cardboard honeycomb with wood or MDF stiles and a solid lock block. It weighs under about 30 lb (14 kg) for 30 x 80 in, about 2 to 3 times lighter than a solid-core door at 50 to 80 lb (23 to 36 kg). One lab study puts its first panel modes near 150 to 170 Hz, with a sound-insulation trough at 125 to 200 Hz. The flimsy skins and the air cavity give the "thin, boxy" knock. A hollow metal (steel) door of 18-gauge steel skins on honeycomb, polystyrene or steel stiffeners is about 3.5 lb/ft²: about 70 to 74 lb (32 to 33 kg) for 36 x 80 to 84 in. In a hollow steel frame it rings and booms unless silencers, cores and grout damp it.

### Cited Findings
- Hollow-core: "1/8" molded hardboard skin"; core empty or cardboard honeycomb; wood or MDF stiles on the vertical edges; a single fibre rail top and bottom; solid wood blocking where the knob and latch go — [ReeB molded hollow core doors](https://learn.reeb.com/knowledge-base/molded-hollow-core-doors/); [This Old House](https://www.thisoldhouse.com/doors/inside-interior-doors) (search summary).
- Weights for 30 x 80 in: hollow core 25 to 35 lb (often under 30); solid core 50 to 80 lb. Typical STC 15 to 20 for hollow against 25 to 30 for solid — [search summary of door vendors, e.g. Doors Los Angeles](https://doorslosangeles.com/blog/solid-core-vs-hollow-core-doors) (vendor figures).
- Lab study of 36 mm wooden door leaves with 5 mm MDF or 6 mm LVL skins:
  - Hollow extruded particleboard core (298 kg/m³): Rw 24 dB as a leaf, about 19 dB as a full assembly.
  - Homogeneous core (397 kg/m³): Rw 27 dB as a leaf, about 24 dB as an assembly.
  - First two modes 146.99 and 170.46 Hz, with a sound-insulation valley at 160 Hz and a trough between 125 and 200 Hz.
  - The core sets low-frequency behaviour; the seals dominate above 500 Hz; gaps cost 8 to 9 dB at mid to high frequencies.
  — [PMC12307673](https://pmc.ncbi.nlm.nih.gov/articles/PMC12307673/).
- Hollow metal: 18-gauge honeycomb-core doors weigh about 3.5 lb/ft². A 36 x 80 in door is about 70 lb and a 36 x 84 in about 73.5 lb — [doorclosersusa, hollow metal door weight](https://www.doorclosersusa.com/How-Much-Does-a-Hollow-Metal-Door-Weigh--s/36028.htm).
- Steel door cores: honeycomb (where insulation is not needed), polystyrene, polyurethane, steel-stiffened ("high traffic, non-aesthetic applications"), and temperature-rise (fire) — [SDI FAQ](https://steeldoor.org/faqs/).
- A156.3 test doors are 1-3/4 in (45 mm) thick, 3 x 7 ft — [BHMA A156.3 draft](https://buildershardware.com/LinkClick.aspx?fileticket=kJ3qTCDM_Ew%3D&portalid=0).

### Inferences
- **Leaf mass and closing energy:**
  - Hollow-core wood: about 11 to 14 kg.
  - Solid-core wood: about 23 to 36 kg.
  - Hollow metal: about 30 to 35 kg, more with a heavy core or exit device.
  - A leaf with moment of inertia I = m·w²/3 at edge speed v carries kinetic energy (1/6)·m·v². For a 33 kg steel leaf at 1 m/s that is about 5.5 J. For a 12 kg hollow-core leaf at the same speed it is about 2 J.
- **Body of the thump:**
  - Hollow-core wood: thin skins over an air gap, light, with panel modes in the 100 to 200 Hz region from the study above, plus skin-cavity resonances. Heard as "boxy" or "papery", with rattle if the skins or lock block are loose.
  - Solid-core wood: heavier and more damped, a duller and deeper thud.
  - Hollow metal: thin steel skins on a light core are lightly damped and ring, the more so with honeycomb or hollow stiffened cores. The hollow steel frame (ungrouted, in drywall) is a second ringing body; silencers decouple the leaf from it.
- Exit devices mount on the steel skin, so their impacts (section 3) drive the whole leaf as a soundboard. That is part of why a push bar on a steel door is so much louder than the mechanism alone.

### Gaps
- No measured modal frequencies or damping for hollow metal doors and frames.
- No published comparison of slam spectra across hollow-core, solid-core and steel doors.
- Weights and STC values for wood doors come from vendor pages, not lab data (apart from the PMC study, which used a specific Chinese wooden-door construction rather than US hollow-core).
