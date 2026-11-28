// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
//! One client connection: the handshake, then one drive either listed in full (ENTRIES mode)
//! or followed through its change journal (FOLLOW mode) until the client hangs up.

use crate::ffi::*;
use crate::mft;
use crate::privacy::Privacy;
use crate::protocol::*;
use crate::snapshot::{Folders, ATTRIBUTE_DIRECTORY};
use crate::volume::*;
use std::collections::{HashMap, HashSet};
use std::time::{Duration, Instant};

const REFERENCE_MASK: u64 = 0xFFFF_FFFF_FFFF;
const REASON_DATA_OVERWRITE: u32 = 0x1;
const REASON_DATA_EXTEND: u32 = 0x2;
const REASON_DATA_TRUNCATION: u32 = 0x4;
const REASON_FILE_CREATE: u32 = 0x100;
const REASON_FILE_DELETE: u32 = 0x200;
const REASON_RENAME_OLD_NAME: u32 = 0x1000;
const REASON_RENAME_NEW_NAME: u32 = 0x2000;
const REASON_BASIC_INFO_CHANGE: u32 = 0x8000;
const REASON_HARD_LINK_CHANGE: u32 = 0x10000;
const REASON_CLOSE: u32 = 0x8000_0000;
const LOOK_AGAIN: u32 = REASON_FILE_CREATE | REASON_DATA_OVERWRITE | REASON_DATA_EXTEND | REASON_DATA_TRUNCATION
    | REASON_BASIC_INFO_CHANGE | REASON_HARD_LINK_CHANGE;

/// Entries per frame, and changes per frame.
const ENTRY_FRAME_BYTES: usize = 1 << 20;
const CHANGES_PER_FRAME: usize = 4000;

pub fn serve(pipe: Pipe, privacy: Privacy) {
    let _ = run(&pipe, &privacy);
    unsafe {
        FlushFileBuffers(pipe.handle.0);
        DisconnectNamedPipe(pipe.handle.0);
    }
}

fn run(pipe: &Pipe, privacy: &Privacy) -> Result<(), Gone> {
    let (kind, body) = pipe.read_frame()?;
    let mut fields = Fields::new(&body);
    if kind != HELLO || fields.u32() != Some(MAGIC) {
        return error(pipe, ERROR_REQUEST, "expected HELLO");
    }
    if fields.u16() != Some(VERSION) {
        return error(pipe, ERROR_VERSION, "protocol version differs");
    }
    Frame::new(HELLO_OK).u32(MAGIC).u16(VERSION).send(pipe)?;

    let (kind, body) = pipe.read_frame()?;
    let mut fields = Fields::new(&body);
    let (Some(letter), Some(mode), Some(journal), Some(usn)) = (fields.u16(), fields.u8(), fields.u64(), fields.i64()) else {
        return error(pipe, ERROR_REQUEST, "expected SUBSCRIBE");
    };
    let letter = (letter as u8 as char).to_ascii_uppercase() as u16;
    if kind != SUBSCRIBE || !(b'A' as u16..=b'Z' as u16).contains(&letter) {
        return error(pipe, ERROR_REQUEST, "expected SUBSCRIBE");
    }
    if !is_fixed_ntfs(letter) {
        return error(pipe, ERROR_NOT_NTFS, "not a fixed NTFS drive");
    }
    let volume = match Volume::open(letter) {
        Ok(volume) => volume,
        Err(problem) => return error(pipe, ERROR_VOLUME, &format!("the drive could not be opened (error {})", problem.0)),
    };
    match mode {
        MODE_ENTRIES => entries(pipe, &volume, privacy),
        MODE_FOLLOW => follow(pipe, &volume, privacy, journal, usn),
        _ => error(pipe, ERROR_REQUEST, "unknown mode"),
    }
}

/// Every file and folder of the drive, folder by folder from the top.
fn entries(pipe: &Pipe, volume: &Volume, privacy: &Privacy) -> Result<(), Gone> {
    let journal = volume.journal().ok();
    let started = Instant::now();
    let (snapshot, source) = match mft::read(volume, false) {
        Ok(read) => read,
        Err(problem) => return error(pipe, ERROR_VOLUME, &problem),
    };
    let order = snapshot.order(privacy, volume.letter);
    crate::log(&format!("{}: {} entries read in {:.1} s", volume.letter as u8 as char, order.len(), started.elapsed().as_secs_f64()));
    let mut frame = Frame::with_capacity(ENTRIES, ENTRY_FRAME_BYTES + 1024);
    let mut count_at = frame.mark();
    frame.u32(0);
    let mut count = 0u32;
    for index in order {
        let name = snapshot.names[index as usize];
        let owner = name.owner as usize;
        let directory = snapshot.is_dir(name.owner);
        let attributes = snapshot.attributes[owner] | if directory { ATTRIBUTE_DIRECTORY } else { 0 };
        frame.u64(name.owner as u64).u64(name.parent as u64).u32(attributes)
            .i64(if directory { 0 } else { snapshot.size[owner] as i64 }).i64(snapshot.modified[owner])
            .text(snapshot.name(index as usize));
        count += 1;
        if frame.len() >= ENTRY_FRAME_BYTES {
            frame.put_u32(count_at, count);
            frame.send(pipe)?;
            frame = Frame::with_capacity(ENTRIES, ENTRY_FRAME_BYTES + 1024);
            count_at = frame.mark();
            frame.u32(0);
            count = 0;
        }
    }
    if count > 0 {
        frame.put_u32(count_at, count);
        frame.send(pipe)?;
    }
    let (id, usn) = journal.map_or((0, 0), |journal| (journal.UsnJournalID, journal.NextUsn));
    Frame::new(END).u64(id).i64(usn).u8(source as u8).send(pipe)
}

/// Why following stopped.
enum Stop {
    Gone,
    /// The journal could not be read from where we were: wrapped, deleted, made anew.
    Journal,
}

impl From<Gone> for Stop {
    fn from(_: Gone) -> Stop {
        Stop::Gone
    }
}

/// Changes on the drive from the client's position (or from now), then as they happen.
fn follow(pipe: &Pipe, volume: &Volume, privacy: &Privacy, client_journal: u64, client_usn: i64) -> Result<(), Gone> {
    let mut wanted = Some((client_journal, client_usn)).filter(|&(id, _)| id != 0);
    loop {
        let Ok(before) = volume.journal() else {
            return Frame::new(NO_JOURNAL).send(pipe);
        };
        // The folders, to turn journal records (a folder and a name) into paths.
        let folders = match mft::read(volume, true) {
            Ok((snapshot, _)) => snapshot.folders(),
            Err(problem) => return error(pipe, ERROR_VOLUME, &problem),
        };
        let Ok(after) = volume.journal() else {
            return Frame::new(NO_JOURNAL).send(pipe);
        };
        if after.UsnJournalID != before.UsnJournalID {
            continue; // made anew while the folders were read: read them again
        }
        let start = match wanted.take() {
            Some((id, usn)) if id == after.UsnJournalID && usn >= after.FirstUsn.max(after.LowestValidUsn) && usn <= after.NextUsn => usn,
            Some(_) => {
                Frame::new(RESCAN).send(pipe)?;
                before.NextUsn
            }
            None => before.NextUsn,
        };
        Frame::new(FOLLOWING).u64(after.UsnJournalID).i64(start).send(pipe)?;
        let mut tracker = Tracker { folders, renaming: HashMap::new(), letter: volume.letter, privacy };
        match tracker.follow(pipe, volume, after.UsnJournalID, start, after.NextUsn) {
            Stop::Gone => return Err(Gone),
            Stop::Journal => {
                crate::log(&format!("{}: the journal could not be read on; starting over", volume.letter as u8 as char));
                if volume.journal().is_err() {
                    return Frame::new(NO_JOURNAL).send(pipe);
                }
                Frame::new(RESCAN).send(pipe)?;
            }
        }
    }
}

enum Change {
    Touch(Vec<u16>),
    Renamed(Vec<u16>, Vec<u16>),
}

struct Tracker<'a> {
    folders: Folders,
    /// Old paths of renames whose new name has not come yet.
    renaming: HashMap<u32, Vec<u16>>,
    letter: u16,
    privacy: &'a Privacy,
}

impl Tracker<'_> {
    fn follow(&mut self, pipe: &Pipe, volume: &Volume, journal: u64, start: i64, caught_up: i64) -> Stop {
        match self.run(pipe, volume, journal, start, caught_up) {
            Ok(never) => match never {},
            Err(stop) => stop,
        }
    }

    fn run(&mut self, pipe: &Pipe, volume: &Volume, journal: u64, start: i64, caught_up: i64) -> Result<std::convert::Infallible, Stop> {
        let mut buffer = vec![0u8; 256 << 10];
        let mut usn = start;
        // Catching up: what changed while the client was away, each path once, renames as two
        // paths to look at (the folders known now may be newer than those records).
        let mut seen = HashSet::new();
        let mut paths = Vec::new();
        while usn < caught_up {
            let got = volume.read_journal(journal, usn, &mut buffer).map_err(|_| Stop::Journal)?;
            let next = u64_at(&buffer, 0) as i64;
            if got <= 8 || next <= usn {
                break;
            }
            let mut changes = Vec::new();
            self.records(&buffer[..got], true, &mut changes);
            for change in changes {
                if let Change::Touch(path) = change {
                    if seen.insert(path.clone()) {
                        paths.push(path);
                    }
                }
            }
            usn = next;
        }
        drop(seen);
        let total = paths.len();
        let mut sent = 0;
        let mut pieces = paths.chunks(CHANGES_PER_FRAME).peekable();
        if pieces.peek().is_none() {
            send(pipe, &[], journal, usn)?;
        }
        while let Some(piece) = pieces.next() {
            sent += piece.len();
            let changes: Vec<Change> = piece.iter().map(|path| Change::Touch(path.clone())).collect();
            // Only the last frame moves the client's position on: until then, a restart catches up again from the start.
            send(pipe, &changes, journal, if pieces.peek().is_none() { usn } else { start })?;
        }
        if total > 0 {
            crate::log(&format!("{}: caught up with {} changed paths", self.letter as u8 as char, sent));
        }
        // Then as it happens, about once a second.
        let mut last_sent = Instant::now();
        loop {
            let got = volume.read_journal(journal, usn, &mut buffer).map_err(|_| Stop::Journal)?;
            let next = u64_at(&buffer, 0) as i64;
            if got > 8 && next > usn {
                let mut changes = Vec::new();
                self.records(&buffer[..got], false, &mut changes);
                // Only the last frame of a read moves the client's position on to after it.
                for (number, piece) in changes.chunks(CHANGES_PER_FRAME).enumerate() {
                    let last = (number + 1) * CHANGES_PER_FRAME >= changes.len();
                    send(pipe, piece, journal, if last { next } else { usn })?;
                }
                usn = next;
                if changes.is_empty() {
                    send(pipe, &[], journal, usn)?;
                }
                last_sent = Instant::now();
                if got > buffer.len() / 2 {
                    continue; // more is waiting
                }
            } else if last_sent.elapsed() > Duration::from_secs(15) {
                // Nothing happened: say so, which also finds out whether the client is still there.
                send(pipe, &[], journal, usn)?;
                last_sent = Instant::now();
            }
            std::thread::sleep(Duration::from_millis(1000));
        }
    }

    /// Turn a buffer of journal records into changes.
    fn records(&mut self, buffer: &[u8], catching_up: bool, changes: &mut Vec<Change>) {
        let mut at = 8;
        while at + 60 <= buffer.len() {
            let length = u32_at(buffer, at) as usize;
            if length < 60 || at + length > buffer.len() {
                break;
            }
            if u16_at(buffer, at + 4) == 2 {
                self.record(&buffer[at..at + length], catching_up, changes);
            }
            at += length;
        }
    }

    fn record(&mut self, record: &[u8], catching_up: bool, changes: &mut Vec<Change>) {
        let id = u64_at(record, 8) & REFERENCE_MASK;
        let parent = u64_at(record, 16) & REFERENCE_MASK;
        if id > u32::MAX as u64 || parent > u32::MAX as u64 {
            return;
        }
        let (id, parent) = (id as u32, parent as u32);
        let reason = u32_at(record, 40);
        let directory = u32_at(record, 52) & ATTRIBUTE_DIRECTORY != 0;
        let count = u16_at(record, 56) as usize / 2;
        let Some(name) = utf16_at(record, u16_at(record, 58) as usize, count) else {
            return;
        };
        if directory && reason & (REASON_FILE_CREATE | REASON_RENAME_NEW_NAME) != 0 {
            self.folders.insert(id, parent, &name);
        }
        let Some(mut path) = self.folders.path(self.letter, parent) else {
            if directory && reason & REASON_FILE_DELETE != 0 {
                self.folders.remove(id);
            }
            return; // in NTFS's own folders, or a folder we never knew
        };
        path.push(b'\\' as u16);
        path.extend_from_slice(&name);
        if reason & REASON_RENAME_OLD_NAME != 0 {
            self.renaming.insert(id, path.clone());
        }
        if reason & REASON_RENAME_NEW_NAME != 0 {
            match self.renaming.remove(&id) {
                Some(old) if !catching_up && self.privacy.allows(&old) && self.privacy.allows(&path) => {
                    changes.push(Change::Renamed(old, path.clone()));
                }
                Some(old) => {
                    self.touch(old, changes);
                    self.touch(path.clone(), changes);
                }
                None => self.touch(path.clone(), changes),
            }
        }
        if reason & REASON_FILE_DELETE != 0 {
            self.renaming.remove(&id);
            if directory {
                self.folders.remove(id);
            }
            self.touch(path, changes);
        } else if reason & REASON_CLOSE != 0 && reason & LOOK_AGAIN != 0 {
            self.touch(path, changes);
        }
    }

    fn touch(&self, path: Vec<u16>, changes: &mut Vec<Change>) {
        if self.privacy.allows(&path) {
            changes.push(Change::Touch(path));
        }
    }
}

fn write_changes(frame: &mut Frame, changes: &[Change]) {
    frame.u32(changes.len() as u32);
    for change in changes {
        match change {
            Change::Touch(path) => {
                frame.u8(TOUCH).text(path);
            }
            Change::Renamed(old, new) => {
                frame.u8(RENAMED).text(new).text(old);
            }
        }
    }
}

fn send(pipe: &Pipe, changes: &[Change], journal: u64, usn: i64) -> Result<(), Gone> {
    let mut frame = Frame::new(CHANGES);
    write_changes(&mut frame, changes);
    frame.u64(journal).i64(usn).send(pipe)
}
