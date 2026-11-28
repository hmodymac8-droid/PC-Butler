// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
//! What one read of a drive found: per file record its flags, attributes, size and date, and
//! every name it has (a hard-linked file has several, each in its own folder).

use crate::privacy::Privacy;
use std::collections::{HashMap, VecDeque};

/// The record of a drive's top folder.
pub const ROOT: u32 = 5;
/// Records below this are NTFS's own files ($MFT, $LogFile, $Extend...), never shown.
pub const FIRST_USER_RECORD: u32 = 16;

pub const IN_USE: u8 = 1;
pub const DIRECTORY: u8 = 2;

pub const ATTRIBUTE_DIRECTORY: u32 = 0x10;

#[derive(Clone, Copy)]
pub struct Name {
    pub owner: u32,
    pub parent: u32,
    pub start: u32,
    pub len: u16,
}

#[derive(Default)]
pub struct Snapshot {
    pub flags: Vec<u8>,
    pub attributes: Vec<u32>,
    pub size: Vec<u64>,
    pub modified: Vec<i64>,
    pub names: Vec<Name>,
    pub pool: Vec<u16>,
}

impl Snapshot {
    pub fn with_records(records: usize) -> Snapshot {
        Snapshot {
            flags: vec![0; records],
            attributes: vec![0; records],
            size: vec![0; records],
            modified: vec![0; records],
            names: Vec::with_capacity(records),
            pool: Vec::with_capacity(records * 16),
        }
    }

    /// Make room for this record (the journal's enumeration does not say how many there are).
    pub fn ensure(&mut self, record: u32) {
        let need = record as usize + 1;
        if self.flags.len() < need {
            let to = need.max(self.flags.len() * 3 / 2);
            self.flags.resize(to, 0);
            self.attributes.resize(to, 0);
            self.size.resize(to, 0);
            self.modified.resize(to, 0);
        }
    }

    pub fn records(&self) -> usize {
        self.flags.len()
    }

    pub fn add_name(&mut self, owner: u32, parent: u32, name: &[u16]) {
        if name.is_empty() || name.len() > u16::MAX as usize || self.pool.len() + name.len() > u32::MAX as usize {
            return;
        }
        let start = self.pool.len() as u32;
        self.pool.extend_from_slice(name);
        self.names.push(Name { owner, parent, start, len: name.len() as u16 });
    }

    pub fn name(&self, index: usize) -> &[u16] {
        let name = self.names[index];
        &self.pool[name.start as usize..name.start as usize + name.len as usize]
    }

    pub fn is_dir(&self, record: u32) -> bool {
        self.flags.get(record as usize).is_some_and(|flags| flags & DIRECTORY != 0)
    }

    fn shown(&self, record: u32) -> bool {
        record >= FIRST_USER_RECORD && self.flags.get(record as usize).is_some_and(|flags| flags & IN_USE != 0)
    }

    /// Per record the first of its children's names, and per name the next one in the same folder.
    fn children(&self) -> (Vec<u32>, Vec<u32>) {
        let mut first = vec![u32::MAX; self.records()];
        let mut next = vec![u32::MAX; self.names.len()];
        // Backwards, so each folder's list keeps the order the names were found in.
        for index in (0..self.names.len()).rev() {
            let parent = self.names[index].parent as usize;
            if parent < first.len() && self.shown(self.names[index].owner) {
                next[index] = first[parent];
                first[parent] = index as u32;
            }
        }
        (first, next)
    }

    /// Find a folder by its path below the top folder, comparing names without regard to case.
    fn locate(&self, first: &[u32], next: &[u32], parts: &[String]) -> Option<u32> {
        let mut at = ROOT;
        for part in parts {
            let mut child = first[at as usize];
            let mut found = None;
            while child != u32::MAX {
                let owner = self.names[child as usize].owner;
                if self.is_dir(owner) && String::from_utf16_lossy(self.name(child as usize)).to_lowercase() == *part {
                    found = Some(owner);
                    break;
                }
                child = next[child as usize];
            }
            at = found?;
        }
        Some(at)
    }

    /// The names to send, folder by folder from the top: a folder always comes before what is in
    /// it, and the names in one folder come together. Other users' profile folders are left out.
    pub fn order(&self, privacy: &Privacy, letter: u16) -> Vec<u32> {
        if ROOT as usize >= self.records() {
            return Vec::new();
        }
        let (first, next) = self.children();
        let users = privacy.parts_on(letter).and_then(|parts| self.locate(&first, &next, parts));
        let mut order = Vec::with_capacity(self.names.len());
        let mut visited = vec![false; self.records()];
        let mut queue = VecDeque::new();
        if ROOT as usize >= self.records() {
            return order;
        }
        visited[ROOT as usize] = true;
        queue.push_back(ROOT);
        while let Some(folder) = queue.pop_front() {
            let mut child = first[folder as usize];
            while child != u32::MAX {
                let index = child as usize;
                child = next[index];
                let owner = self.names[index].owner;
                let dir = self.is_dir(owner);
                if dir && Some(folder) == users && !privacy.allowed_profile(self.name(index)) {
                    continue;
                }
                order.push(index as u32);
                if dir && !visited[owner as usize] {
                    visited[owner as usize] = true;
                    queue.push_back(owner);
                }
            }
        }
        order
    }

    /// Only the folders, to turn journal records (a parent folder and a name) into full paths.
    pub fn folders(&self) -> Folders {
        let mut map = HashMap::with_capacity(self.names.len() / 8);
        for index in 0..self.names.len() {
            let name = self.names[index];
            if self.is_dir(name.owner) && self.shown(name.owner) {
                map.entry(name.owner).or_insert_with(|| (name.parent, self.name(index).to_vec().into_boxed_slice()));
            }
        }
        Folders { map }
    }
}

/// Every folder of a drive: its parent and its name, kept current from the journal.
pub struct Folders {
    map: HashMap<u32, (u32, Box<[u16]>)>,
}

impl Folders {
    pub fn insert(&mut self, record: u32, parent: u32, name: &[u16]) {
        if record >= FIRST_USER_RECORD {
            self.map.insert(record, (parent, name.to_vec().into_boxed_slice()));
        }
    }

    pub fn remove(&mut self, record: u32) {
        self.map.remove(&record);
    }

    /// "C:\Users\me" for a folder's record; None when its chain does not reach the top (NTFS's own folders).
    pub fn path(&self, letter: u16, record: u32) -> Option<Vec<u16>> {
        let mut parts: Vec<&[u16]> = Vec::new();
        let mut at = record;
        while at != ROOT {
            let (parent, name) = self.map.get(&at)?;
            parts.push(name);
            at = *parent;
            if parts.len() > 4096 {
                return None;
            }
        }
        let mut path = vec![letter, b':' as u16];
        for part in parts.iter().rev() {
            path.push(b'\\' as u16);
            path.extend_from_slice(part);
        }
        Some(path)
    }
}
