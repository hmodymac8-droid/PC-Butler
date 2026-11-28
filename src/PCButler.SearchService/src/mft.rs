// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
//! Reading a drive's file table, $MFT, straight from the disk in large sequential pieces: every
//! file record's names, size, date modified and attributes. When $MFT cannot be read that way,
//! the change journal's enumeration (FSCTL_ENUM_USN_DATA) gives the names and folders instead.

use crate::ffi::{ERROR_HANDLE_EOF, Background};
use crate::snapshot::*;
use crate::volume::*;

const FILE_SIGNATURE: u32 = 0x454C_4946; // "FILE"
const REFERENCE_MASK: u64 = 0xFFFF_FFFF_FFFF;
const DATA_SEEN: u8 = 4;
const CHUNK: usize = 4 << 20;

/// Read the drive. folders_only keeps only the folders' names (to follow the journal).
pub fn read(volume: &Volume, folders_only: bool) -> Result<(Snapshot, Source), String> {
    let _quiet = Background::begin();
    match read_mft(volume, folders_only) {
        Ok(snapshot) => Ok((snapshot, Source::Mft)),
        Err(problem) => {
            crate::log(&format!("{}: $MFT could not be read ({}), enumerating through the journal", volume.letter as u8 as char, problem));
            enumerate(volume, folders_only).map(|snapshot| (snapshot, Source::Enumeration))
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Source {
    Mft = 1,
    Enumeration = 2,
}

struct Layout {
    sector: u64,
    cluster: u64,
    record: usize,
    mft_lcn: u64,
}

fn layout(volume: &Volume) -> Result<Layout, String> {
    let mut boot = vec![0u8; 4096];
    volume.read_at(0, &mut boot[..512]).map_err(|e| format!("boot sector: error {}", e.0))?;
    if &boot[3..11] != b"NTFS    " {
        return Err("not NTFS".into());
    }
    let sector = u16_at(&boot, 0x0B) as u64;
    if !(512..=4096).contains(&sector) || !sector.is_power_of_two() {
        return Err(format!("sector size {}", sector));
    }
    let per_cluster = boot[0x0D] as u32;
    let per_cluster = if per_cluster <= 0x80 { per_cluster as u64 } else { 1u64 << (256 - per_cluster).min(31) };
    let cluster = sector * per_cluster;
    let raw = boot[0x40] as i8;
    let record = if raw > 0 { raw as u64 * cluster } else { 1u64 << (-(raw as i32)).clamp(0, 20) };
    if !(256..=65536).contains(&record) || cluster == 0 {
        return Err(format!("record size {}", record));
    }
    Ok(Layout { sector, cluster, record: record as usize, mft_lcn: u64_at(&boot, 0x30) })
}

/// Undo NTFS's update sequence: the last two bytes of each 512-byte piece were swapped out on
/// writing. False for a record torn by a crash.
fn fixup(record: &mut [u8]) -> bool {
    let offset = u16_at(record, 4) as usize;
    let count = u16_at(record, 6) as usize;
    if count < 2 || offset + count * 2 > record.len() {
        return false;
    }
    let check = [record[offset], record[offset + 1]];
    for piece in 1..count {
        let end = piece * 512;
        if end > record.len() {
            break;
        }
        if record[end - 2..end] != check {
            return false;
        }
        record[end - 2] = record[offset + piece * 2];
        record[end - 1] = record[offset + piece * 2 + 1];
    }
    true
}

/// A non-resident attribute's runs: (first cluster, or -1 for a hole; clusters).
fn runs(record: &[u8], mut at: usize) -> Vec<(i64, u64)> {
    let mut list = Vec::new();
    let mut lcn = 0i64;
    while at < record.len() && record[at] != 0 {
        let header = record[at];
        let (length_bytes, offset_bytes) = ((header & 0xF) as usize, (header >> 4) as usize);
        at += 1;
        if length_bytes == 0 || length_bytes > 8 || offset_bytes > 8 || at + length_bytes + offset_bytes > record.len() {
            break;
        }
        let mut length = 0u64;
        for byte in 0..length_bytes {
            length |= (record[at + byte] as u64) << (8 * byte);
        }
        at += length_bytes;
        if offset_bytes == 0 {
            list.push((-1, length));
            continue;
        }
        let mut delta = 0i64;
        for byte in 0..offset_bytes {
            delta |= (record[at + byte] as i64) << (8 * byte);
        }
        if record[at + offset_bytes - 1] & 0x80 != 0 && offset_bytes < 8 {
            delta |= -1i64 << (8 * offset_bytes);
        }
        at += offset_bytes;
        lcn += delta;
        list.push((lcn, length));
    }
    list
}

fn read_mft(volume: &Volume, folders_only: bool) -> Result<Snapshot, String> {
    let layout = layout(volume)?;
    let first = (layout.record as u64).max(layout.cluster).div_ceil(layout.sector) * layout.sector;
    let mut zero = vec![0u8; first as usize];
    volume.read_at(layout.mft_lcn * layout.cluster, &mut zero).map_err(|e| format!("record 0: error {}", e.0))?;
    let zero = &mut zero[..layout.record];
    if u32_at(zero, 0) != FILE_SIGNATURE || !fixup(zero) {
        return Err("record 0 is damaged".into());
    }
    // $MFT's own unnamed $DATA attribute says where the table lies on the disk.
    let (mut extents, mut bytes) = (Vec::new(), 0u64);
    let used = (u32_at(zero, 0x18) as usize).min(layout.record);
    let mut at = u16_at(zero, 0x14) as usize;
    while at + 16 <= used {
        let kind = u32_at(zero, at);
        let length = u32_at(zero, at + 4) as usize;
        if kind == 0xFFFF_FFFF || length < 16 || at + length > used {
            break;
        }
        if kind == 0x80 && zero[at + 8] != 0 && zero[at + 9] == 0 {
            bytes = u64_at(zero, at + 0x30);
            extents = runs(&zero[..at + length], at + u16_at(zero, at + 0x20) as usize);
            break;
        }
        at += length;
    }
    let covered: u64 = extents.iter().map(|&(_, clusters)| clusters * layout.cluster).sum();
    if extents.is_empty() || bytes == 0 || covered < bytes {
        // The rest of the list sits in other records (a very fragmented $MFT): not handled here.
        return Err("the $MFT's own extents are incomplete".into());
    }
    let records = (bytes / layout.record as u64) as usize;
    if records > u32::MAX as usize {
        return Err("too many records".into());
    }
    let mut snapshot = Snapshot::with_records(records);
    let chunk = (CHUNK / layout.record).max(1) * layout.record;
    let mut buffer = vec![0u8; chunk + layout.sector as usize];
    let mut number = 0usize;
    for (lcn, clusters) in extents {
        let run = clusters * layout.cluster;
        let mut offset = 0u64;
        while offset < run && number < records {
            let left = ((records - number) * layout.record) as u64;
            let wanted = (chunk as u64).min(run - offset).min(left);
            let size = (wanted.div_ceil(layout.sector) * layout.sector).min(run - offset) as usize;
            let piece = &mut buffer[..size];
            if lcn < 0 {
                piece.fill(0);
            } else {
                volume.read_at(lcn as u64 * layout.cluster + offset, piece).map_err(|e| format!("reading $MFT: error {}", e.0))?;
            }
            let mut start = 0;
            while start + layout.record <= size && number < records {
                parse(&mut snapshot, number as u32, &mut piece[start..start + layout.record], folders_only);
                start += layout.record;
                number += 1;
            }
            offset += size as u64;
        }
    }
    Ok(snapshot)
}

fn parse(snapshot: &mut Snapshot, number: u32, record: &mut [u8], folders_only: bool) {
    if u32_at(record, 0) != FILE_SIGNATURE || !fixup(record) {
        return;
    }
    let header = u16_at(record, 0x16);
    if header & 1 == 0 {
        return;
    }
    let base = u64_at(record, 0x20) & REFERENCE_MASK;
    let owner = if base != 0 { base } else { number as u64 };
    if owner as usize >= snapshot.records() {
        return;
    }
    let owner = owner as u32;
    let slot = owner as usize;
    let directory = header & 2 != 0;
    if base == 0 {
        snapshot.flags[slot] |= IN_USE | if directory { DIRECTORY } else { 0 };
    }
    let used = (u32_at(record, 0x18) as usize).min(record.len());
    let mut at = u16_at(record, 0x14) as usize;
    while at + 16 <= used {
        let kind = u32_at(record, at);
        let length = u32_at(record, at + 4) as usize;
        if kind == 0xFFFF_FFFF || length < 16 || at + length > used {
            break;
        }
        let resident = record[at + 8] == 0;
        let named = record[at + 9] != 0;
        let value = at + u16_at(record, at + 0x14) as usize;
        let value_length = u32_at(record, at + 0x10) as usize;
        match kind {
            // $STANDARD_INFORMATION: dates and attributes.
            0x10 if resident && value_length >= 0x24 && value + 0x24 <= at + length => {
                snapshot.modified[slot] = u64_at(record, value + 8) as i64;
                snapshot.attributes[slot] = u32_at(record, value + 0x20);
            }
            // $FILE_NAME: one name in one folder. DOS 8.3 names (namespace 2) are left out.
            0x30 if resident && value + 0x42 <= at + length => {
                let parent = u64_at(record, value) & REFERENCE_MASK;
                let count = record[value + 0x40] as usize;
                let namespace = record[value + 0x41];
                if snapshot.flags[slot] & DATA_SEEN == 0 {
                    snapshot.size[slot] = u64_at(record, value + 0x30);
                }
                if namespace != 2 && parent <= u32::MAX as u64 && (!folders_only || directory || base != 0) {
                    if let Some(name) = utf16_at(record, value + 0x42, count) {
                        snapshot.add_name(owner, parent as u32, &name);
                    }
                }
            }
            // The unnamed $DATA: the file's size (from its first piece when it is spread over records).
            0x80 if !named => {
                if resident {
                    snapshot.size[slot] = value_length as u64;
                    snapshot.flags[slot] |= DATA_SEEN;
                } else if u64_at(record, at + 0x10) == 0 {
                    snapshot.size[slot] = u64_at(record, at + 0x30);
                    snapshot.flags[slot] |= DATA_SEEN;
                }
            }
            _ => {}
        }
        at += length;
    }
}

/// The slower way: the journal lists every record's name, folder and attributes (no sizes or dates).
fn enumerate(volume: &Volume, folders_only: bool) -> Result<Snapshot, String> {
    let high = volume.journal().map(|journal| journal.NextUsn).unwrap_or(i64::MAX);
    let mut snapshot = Snapshot::with_records(1 << 16);
    snapshot.ensure(ROOT);
    let mut buffer = vec![0u8; 1 << 20];
    let mut start = 0u64;
    loop {
        let got = match volume.enumerate(start, high, &mut buffer) {
            Ok(got) => got,
            Err(WinError(ERROR_HANDLE_EOF)) => break,
            Err(error) => return Err(format!("enumeration: error {}", error.0)),
        };
        if got <= 8 {
            break;
        }
        start = u64_at(&buffer, 0);
        let mut at = 8;
        while at + 60 <= got {
            let length = u32_at(&buffer, at) as usize;
            if length < 60 || at + length > got {
                break;
            }
            if u16_at(&buffer, at + 4) == 2 {
                let record = u64_at(&buffer, at + 8) & REFERENCE_MASK;
                let parent = u64_at(&buffer, at + 16) & REFERENCE_MASK;
                let attributes = u32_at(&buffer, at + 52);
                let count = u16_at(&buffer, at + 56) as usize / 2;
                let offset = u16_at(&buffer, at + 58) as usize;
                let directory = attributes & ATTRIBUTE_DIRECTORY != 0;
                if record <= u32::MAX as u64 && parent <= u32::MAX as u64 {
                    let record = record as u32;
                    snapshot.ensure(record);
                    snapshot.flags[record as usize] |= IN_USE | if directory { DIRECTORY } else { 0 };
                    snapshot.attributes[record as usize] = attributes;
                    if !folders_only || directory {
                        if let Some(name) = utf16_at(&buffer[..at + length], at + offset, count) {
                            snapshot.add_name(record, parent as u32, &name);
                        }
                    }
                }
            }
            at += length;
        }
    }
    Ok(snapshot)
}
