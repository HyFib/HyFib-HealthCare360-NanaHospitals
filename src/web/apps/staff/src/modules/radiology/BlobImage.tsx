import { useEffect, useRef, type CSSProperties } from 'react';

/**
 * An image fetched from the server (a Blob), shown with an object URL that is made when it appears and revoked when it
 * changes or leaves, so pictures of patients do not stay in the page's memory. The address is set on the element
 * directly: the image is outside React's state, and this works when React mounts a component twice in development.
 */
export function BlobImage({
  blob,
  alt,
  className,
  style,
}: {
  blob: Blob | undefined;
  alt: string;
  className?: string;
  style?: CSSProperties;
}) {
  const ref = useRef<HTMLImageElement>(null);
  useEffect(() => {
    const image = ref.current;
    if (!image || !blob) {
      return undefined;
    }
    const url = URL.createObjectURL(blob);
    image.src = url;
    return () => {
      image.removeAttribute('src');
      URL.revokeObjectURL(url);
    };
  }, [blob]);
  return <img ref={ref} alt={alt} className={className} style={style} draggable={false} />;
}
