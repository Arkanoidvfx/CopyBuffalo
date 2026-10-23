use pure_onnx_ocr::OcrEngineBuilder;
use serde_json::json;
use std::{env, io::{self, Read}, process};

const MAX_BYTES: u64 = 128 * 1024 * 1024;
const MAX_PIXELS: u64 = 80_000_000;

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<String> = env::args().collect();
    if args.len() != 3 { return Err("usage: copybuffalo-ocr DET_DIR REC_DIR < image".into()); }
    let mut bytes = Vec::new();
    io::stdin().take(MAX_BYTES + 1).read_to_end(&mut bytes)?;
    if bytes.is_empty() || bytes.len() as u64 > MAX_BYTES { return Err("invalid image byte count".into()); }
    let reader = image::ImageReader::new(io::Cursor::new(&bytes)).with_guessed_format()?;
    let (width, height) = reader.into_dimensions()?;
    if width == 0 || height == 0 || u64::from(width) * u64::from(height) > MAX_PIXELS {
        return Err("image exceeds 80 megapixels".into());
    }
    let engine = OcrEngineBuilder::new()
        .det_model_dir(&args[1]).rec_model_dir(&args[2])
        .det_limit_side_len(2048)
        .inference_threads(4).rec_batch_size(1).build()?;
    let results = engine.run_from_bytes(&bytes)?;
    let regions: Vec<_> = results.iter().map(|r| {
        let points: Vec<_> = r.bounding_box.exterior().points().collect();
        let x = points.iter().map(|p| p.x()).fold(f64::INFINITY, f64::min);
        let y = points.iter().map(|p| p.y()).fold(f64::INFINITY, f64::min);
        let right = points.iter().map(|p| p.x()).fold(f64::NEG_INFINITY, f64::max);
        let bottom = points.iter().map(|p| p.y()).fold(f64::NEG_INFINITY, f64::max);
        json!({"text":r.text,"confidence":r.confidence,"box":[x,y,right-x,bottom-y]})
    }).collect();
    println!("{}", json!({"width":width,"height":height,"regions":regions}));
    Ok(())
}

fn main() {
    if run().is_err() {
        // Never echo input, recognized text or image paths to diagnostic logs.
        eprintln!("Local OCR failed: invalid input, missing models or inference error.");
        process::exit(1);
    }
}
