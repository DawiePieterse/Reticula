import { Injectable } from '@angular/core';

const MAX_EDGE = 1600;

/**
 * Re-encodes a camera photo as JPEG with its longest edge at most 1600 px. Re-encoding drops EXIF,
 * including embedded GPS and device details, and keeps uploads small on mobile data.
 * Falls back to the original file if the browser cannot decode it.
 */
@Injectable({ providedIn: 'root' })
export class PhotoService {
  async prepare(file: File): Promise<Blob> {
    try {
      const bitmap = await createImageBitmap(file);
      const scale = Math.min(1, MAX_EDGE / Math.max(bitmap.width, bitmap.height));
      const canvas = document.createElement('canvas');
      canvas.width = Math.round(bitmap.width * scale);
      canvas.height = Math.round(bitmap.height * scale);
      canvas.getContext('2d')!.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
      bitmap.close();
      const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.82));
      return blob ?? file;
    } catch {
      return file;
    }
  }
}
