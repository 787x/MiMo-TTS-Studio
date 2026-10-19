export type Theme='light'|'dark'
export function getTheme():Theme {try{return localStorage.getItem('mimo-theme')==='dark'?'dark':'light'}catch{return 'light'}}
export function setTheme(value:Theme){try{localStorage.setItem('mimo-theme',value)}catch{value='light'}document.documentElement.dataset.theme=value;return value}
