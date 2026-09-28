import BrowserOnly from '@docusaurus/BrowserOnly';
import React from 'react';

const containerStyle = {
  display: 'block',
  minHeight: '42rem',
  width: '100%',
};

export default function LikeC4View({viewId}) {
  return (
    <BrowserOnly fallback={<p>Loading the interactive architecture view…</p>}>
      {() => React.createElement('agentstration-c4-view', {
        'view-id': viewId,
        browser: 'true',
        style: containerStyle,
      })}
    </BrowserOnly>
  );
}
