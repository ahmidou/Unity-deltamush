#Delta Mush deformer

Delta Mush **GPU** implementation via Compute shader.

Contains C# implementation for reference.


## Overview

Delta Mush is a deformer originally introduced by Rhythm & Hues and functions as a low-pass filter to remove deformation artifacts and guide the final result closer to the original rest geometry.

The Delta Mush deformer is based on work done by: *Joe Mancewicz, Matt L. Derksen, and Cyrus A. Wilson, Delta mush: smoothing deformations while preserving detail, SIGGRAPH 2014, ACM, New York, NY, USA, Article 28, 1 pages.*

* Video: https://vimeo.com/103666815
* Talk: http://on-demand.gputechconf.com/gtc/2015/video/S5641.html and http://on-demand.gputechconf.com/gtc/2015/presentation/S5641-Joe-Mancewicz.pdf
* Paper: http://dl.acm.org/citation.cfm?id=2614144 *(behind the pay wall :(, I haven't seen it myself yet)*


### Overview in a single picture

![Comparison.png](https://bitbucket.org/repo/Eg6kznG/images/1122461644-Comparison.png)